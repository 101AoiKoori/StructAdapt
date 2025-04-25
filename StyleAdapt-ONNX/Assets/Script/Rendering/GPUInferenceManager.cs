using System;
using UnityEngine;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using System.Linq;

/// <summary>
/// GPU���ٵ���������� - �Ż��汾
/// </summary>
public class GPUInferenceManager : IDisposable
{
    // ������ɫ�����ں�����
    private ComputeShader computeShader;
    private int textureToTensorKernel;
    private int tensorToTextureKernel;
    private int rgbToSRGBKernel;
    private int srgbToRGBKernel;
    private int combinedTextureToTensorKernel; // ��ϰ汾���ں�

    // ����ִ����
    private InferenceExecutor inferenceExecutor;

    // �������� - ʹ��ComputeBuffer�ر���Ƶ��������ͷ�
    private Dictionary<int, ComputeBuffer> bufferPool = new Dictionary<int, ComputeBuffer>();
    private Dictionary<string, RenderTexture> rtPool = new Dictionary<string, RenderTexture>();

    // ���ܼ��
    private System.Diagnostics.Stopwatch performanceWatch = new System.Diagnostics.Stopwatch();
    private float lastInferenceTime = 0f;
    private int inferenceCount = 0;
    private float totalInferenceTime = 0f;
    private float averageInferenceTime = 0f;

    // ����
    private int batchSize = 1;
    private int channels = 3;
    private int inputWidth = 256;
    private int inputHeight = 256;
    private bool debugLogging = false;
    private bool isDisposed = false;

    // ���ò��д���
    private bool enableParallelProcessing = true;

    // �̶���С�Ļ����� - Ԥ�����Ա���GCѹ��
    private ComputeBuffer contentBuffer;
    private ComputeBuffer styleBuffer;
    private ComputeBuffer outputBuffer;

    public GPUInferenceManager(InferenceExecutor executor, int width = 256, int height = 256, int channels = 3)
    {
        this.inferenceExecutor = executor;
        this.inputWidth = width;
        this.inputHeight = height;
        this.channels = channels;

        InitializeCompute();
        InitializeBuffers();
    }

    private void InitializeCompute()
    {
        // ���ؼ�����ɫ��
        computeShader = Resources.Load<ComputeShader>("Shaders/StyleTransfer");
        if (computeShader == null)
        {
            Debug.LogError("�޷�����StyleTransfer������ɫ����ȷ����λ��Resources/ShadersĿ¼�¡�");
            return;
        }

        // �����ں�
        textureToTensorKernel = computeShader.FindKernel("CSTextureToTensor");
        tensorToTextureKernel = computeShader.FindKernel("CSTensorToTexture");
        rgbToSRGBKernel = computeShader.FindKernel("CSRGBToSRGB");
        srgbToRGBKernel = computeShader.FindKernel("CSSRGBToRGB");
        combinedTextureToTensorKernel = computeShader.FindKernel("CSCombinedTextureToTensor");

        if (debugLogging)
        {
            Debug.Log("GPUInferenceManager: ������ɫ����ʼ���ɹ�");
        }
    }

    private void InitializeBuffers()
    {
        // Ԥ����̶���С�Ļ�����
        int bufferSize = batchSize * channels * inputWidth * inputHeight;
        contentBuffer = new ComputeBuffer(bufferSize, sizeof(float));
        styleBuffer = new ComputeBuffer(bufferSize, sizeof(float));
        outputBuffer = new ComputeBuffer(bufferSize, sizeof(float));

        if (debugLogging)
        {
            Debug.Log($"GPUInferenceManager: Ԥ���仺������С: {bufferSize} ������");
        }
    }

    /// <summary>
    /// ִ�з��Ǩ������ - �Ż��汾
    /// </summary>
    public RenderTexture RunStyleTransfer(RenderTexture contentTexture, Texture styleTexture, RenderTexture outputTexture, Vector4 colorBias)
    {
        if (isDisposed || computeShader == null || inferenceExecutor == null)
        {
            Debug.LogError("GPUInferenceManager �ѱ��ͷŻ�δ��ȷ��ʼ��");
            return outputTexture;
        }

        performanceWatch.Restart();

        try
        {
            // 1. ����׼���������� - ʹ���µ�����ں�ͬʱ�������ݺͷ������
            Tensor<float> contentTensor, styleTensor;
            PrepareInputTensors(contentTexture, styleTexture, out contentTensor, out styleTensor);

            // 2. ִ������
            var outputTensor = inferenceExecutor.RunInference(contentTensor, styleTensor);
            if (outputTensor == null)
            {
                Debug.LogError("�����ؿ�����");
                return outputTexture;
            }

            // 3. ���������ת��������
            TensorToTexture(outputTensor, outputTexture, colorBias);

            // 4. ��������ͳ��
            performanceWatch.Stop();
            lastInferenceTime = performanceWatch.ElapsedMilliseconds / 1000f;
            totalInferenceTime += lastInferenceTime;
            inferenceCount++;
            averageInferenceTime = totalInferenceTime / inferenceCount;

            // ���ڸ���������־
            if (debugLogging && inferenceCount % 10 == 0)
            {
                Debug.Log($"���Ǩ��ͳ��: ƽ��ʱ�� = {averageInferenceTime * 1000:F2}ms, ���һ�� = {lastInferenceTime * 1000:F2}ms, FPS = {1 / lastInferenceTime:F1}");
            }

            return outputTexture;
        }
        catch (Exception e)
        {
            Debug.LogError($"ִ�з��Ǩ��ʱ����: {e.Message}\n{e.StackTrace}");
            return outputTexture;
        }
    }

    /// <summary>
    /// ׼���������� - ʹ��GPU���ٲ��д������ݺͷ������
    /// </summary>
    private void PrepareInputTensors(RenderTexture contentTexture, Texture styleTexture, out Tensor<float> contentTensor, out Tensor<float> styleTensor)
    {
        if (enableParallelProcessing && combinedTextureToTensorKernel >= 0)
        {
            // ��ȡ�򴴽�����������ʱ��Ⱦ����
            RenderTexture styleRT = GetOrCreateRenderTexture(inputWidth, inputHeight, RenderTextureFormat.ARGBFloat);
            Graphics.Blit(styleTexture, styleRT);

            // ��������ں�
            computeShader.SetTexture(combinedTextureToTensorKernel, "_InputTexture", contentTexture);
            computeShader.SetTexture(combinedTextureToTensorKernel, "_StyleTexture", styleRT);
            computeShader.SetBuffer(combinedTextureToTensorKernel, "_ContentBuffer", contentBuffer);
            computeShader.SetBuffer(combinedTextureToTensorKernel, "_StyleBuffer", styleBuffer);
            computeShader.SetInt("_Width", inputWidth);
            computeShader.SetInt("_Height", inputHeight);

            // ���ȼ�����ɫ�� - һ���Դ������ݺͷ��
            int threadGroupsX = Mathf.CeilToInt(inputWidth / 16f);
            int threadGroupsY = Mathf.CeilToInt(inputHeight / 16f);
            computeShader.Dispatch(combinedTextureToTensorKernel, threadGroupsX, threadGroupsY, 1);

            // �ӻ�������������
            contentTensor = CreateTensorFromBuffer(contentBuffer);
            styleTensor = CreateTensorFromBuffer(styleBuffer);
        }
        else
        {
            // �����������ݺͷ�� - ���˷���
            contentTensor = TextureToTensor(contentTexture, contentBuffer);
            styleTensor = TextureToTensor(styleTexture, styleBuffer);
        }
    }

    /// <summary>
    /// �ӻ�������������
    /// </summary>
    private Tensor<float> CreateTensorFromBuffer(ComputeBuffer buffer)
    {
        int bufferSize = batchSize * channels * inputWidth * inputHeight;
        float[] tensorData = new float[bufferSize];
        buffer.GetData(tensorData);

        // �������������
        var tensor = new DenseTensor<float>(new[] { batchSize, channels, inputHeight, inputWidth });

        // ʹ�ò���������������
        Parallel.For(0, batchSize, n =>
        {
            for (int c = 0; c < channels; c++)
            {
                int channelOffset = c * inputWidth * inputHeight;
                for (int h = 0; h < inputHeight; h++)
                {
                    int rowOffset = h * inputWidth;
                    for (int w = 0; w < inputWidth; w++)
                    {
                        int index = n * channels * inputWidth * inputHeight + channelOffset + rowOffset + w;
                        tensor[n, c, h, w] = tensorData[index];
                    }
                }
            }
        });

        return tensor;
    }

    /// <summary>
    /// ����ת���� - �Ż��汾
    /// </summary>
    private Tensor<float> TextureToTensor(Texture texture, ComputeBuffer outputBuffer)
    {
        // ������ʱ����������С
        RenderTexture tempRT = GetOrCreateRenderTexture(inputWidth, inputHeight, RenderTextureFormat.ARGBFloat);
        Graphics.Blit(texture, tempRT);

        // ���ü�����ɫ������
        computeShader.SetTexture(textureToTensorKernel, "_InputTexture", tempRT);
        computeShader.SetBuffer(textureToTensorKernel, "_OutputBuffer", outputBuffer);
        computeShader.SetInt("_Width", inputWidth);
        computeShader.SetInt("_Height", inputHeight);
        computeShader.SetInt("_Channels", channels);
        computeShader.SetInt("_BatchSize", batchSize);

        // ���ȼ�����ɫ��
        int threadGroupsX = Mathf.CeilToInt(inputWidth / 16f);
        int threadGroupsY = Mathf.CeilToInt(inputHeight / 16f);
        computeShader.Dispatch(textureToTensorKernel, threadGroupsX, threadGroupsY, 1);

        // ��ComputeBuffer��ȡ����
        int bufferSize = batchSize * channels * inputWidth * inputHeight;
        float[] tensorData = new float[bufferSize];
        outputBuffer.GetData(tensorData);

        // �����������������
        var tensor = new DenseTensor<float>(new[] { batchSize, channels, inputHeight, inputWidth });

        // ʹ�ò��д����������
        Parallel.For(0, batchSize, n =>
        {
            for (int c = 0; c < channels; c++)
            {
                int channelOffset = c * inputWidth * inputHeight;
                for (int h = 0; h < inputHeight; h++)
                {
                    int rowOffset = h * inputWidth;
                    for (int w = 0; w < inputWidth; w++)
                    {
                        int index = n * channels * inputWidth * inputHeight + channelOffset + rowOffset + w;
                        tensor[n, c, h, w] = tensorData[index];
                    }
                }
            }
        });

        return tensor;
    }

    /// <summary>
    /// ����ת���� - �Ż��汾
    /// </summary>
    private void TensorToTexture(Tensor<float> tensor, RenderTexture target, Vector4 colorBias)
    {
        // ȷ��Ŀ���������������д��
        if (!target.enableRandomWrite)
        {
            target.enableRandomWrite = true;
            target.Create();
        }

        // ���������ݸ��Ƶ����㻺����
        outputBuffer.SetData(tensor.ToArray());

        // ���ü�����ɫ������
        computeShader.SetBuffer(tensorToTextureKernel, "_InputBuffer", outputBuffer);
        computeShader.SetTexture(tensorToTextureKernel, "_OutputTexture", target);
        computeShader.SetVector("_ColorBias", colorBias);
        computeShader.SetInt("_Width", inputWidth);
        computeShader.SetInt("_Height", inputHeight);
        computeShader.SetInt("_Channels", channels);

        // ���ȼ�����ɫ��
        int threadGroupsX = Mathf.CeilToInt(inputWidth / 16f);
        int threadGroupsY = Mathf.CeilToInt(inputHeight / 16f);
        computeShader.Dispatch(tensorToTextureKernel, threadGroupsX, threadGroupsY, 1);
    }

    /// <summary>
    /// ��ɫ�ռ�ת��: RGB �� sRGB
    /// </summary>
    public void ConvertRGBToSRGB(RenderTexture source, RenderTexture destination, Vector4 colorBias)
    {
        // �߽���
        if (source == null || !source.IsCreated() || destination == null || !destination.IsCreated())
        {
            Debug.LogError("��Ч������");
            return;
        }

        // ȷ��Ŀ�������������д��
        if (!destination.enableRandomWrite)
        {
            destination.enableRandomWrite = true;
            destination.Create();
        }

        // ���ü�����ɫ������
        computeShader.SetTexture(rgbToSRGBKernel, "_InputTexture", source);
        computeShader.SetTexture(rgbToSRGBKernel, "_OutputTexture", destination);
        computeShader.SetVector("_ColorBias", colorBias);

        // ���ȼ�����ɫ��
        int threadGroupsX = Mathf.CeilToInt(source.width / 16f);
        int threadGroupsY = Mathf.CeilToInt(source.height / 16f);
        computeShader.Dispatch(rgbToSRGBKernel, threadGroupsX, threadGroupsY, 1);
    }

    /// <summary>
    /// ��ɫ�ռ�ת��: sRGB �� RGB
    /// </summary>
    public void ConvertSRGBToRGB(RenderTexture source, RenderTexture destination, Vector4 colorBias)
    {
        // �߽���
        if (source == null || !source.IsCreated() || destination == null || !destination.IsCreated())
        {
            Debug.LogError("��Ч������");
            return;
        }

        // ȷ��Ŀ�������������д��
        if (!destination.enableRandomWrite)
        {
            destination.enableRandomWrite = true;
            destination.Create();
        }

        // ���ü�����ɫ������
        computeShader.SetTexture(srgbToRGBKernel, "_InputTexture", source);
        computeShader.SetTexture(srgbToRGBKernel, "_OutputTexture", destination);
        computeShader.SetVector("_ColorBias", colorBias);

        // ���ȼ�����ɫ��
        int threadGroupsX = Mathf.CeilToInt(source.width / 16f);
        int threadGroupsY = Mathf.CeilToInt(source.height / 16f);
        computeShader.Dispatch(srgbToRGBKernel, threadGroupsX, threadGroupsY, 1);
    }

    /// <summary>
    /// ��ȡ�򴴽���Ⱦ���� - ����Ƶ������
    /// </summary>
    private RenderTexture GetOrCreateRenderTexture(int width, int height, RenderTextureFormat format)
    {
        string key = $"{width}x{height}_{format}";
        if (rtPool.TryGetValue(key, out RenderTexture rt))
        {
            if (rt != null && rt.width == width && rt.height == height && rt.format == format)
            {
                return rt;
            }

            // ����ߴ���ʽ��ƥ�䣬�ͷžɵ�
            if (rt != null)
            {
                rt.Release();
                UnityEngine.Object.Destroy(rt);
            }
        }

        // �����µ���Ⱦ����
        rt = new RenderTexture(width, height, 0, format);
        rt.enableRandomWrite = true;
        rt.Create();
        rtPool[key] = rt;
        return rt;
    }

    /// <summary>
    /// ��ȡ����ͳ����Ϣ
    /// </summary>
    public Dictionary<string, string> GetPerformanceStats()
    {
        var stats = new Dictionary<string, string>();
        stats.Add("�������", inferenceCount.ToString());
        stats.Add("ƽ������ʱ��", $"{averageInferenceTime * 1000:F2} ms");
        stats.Add("���һ������ʱ��", $"{lastInferenceTime * 1000:F2} ms");
        stats.Add("����FPS", $"{1 / averageInferenceTime:F1}");
        stats.Add("���д���", enableParallelProcessing ? "����" : "����");
        return stats;
    }

    /// <summary>
    /// ������Դ
    /// </summary>
    public void Dispose()
    {
        if (isDisposed)
            return;

        // �ͷ�ComputeBuffer
        if (contentBuffer != null)
        {
            contentBuffer.Release();
            contentBuffer = null;
        }

        if (styleBuffer != null)
        {
            styleBuffer.Release();
            styleBuffer = null;
        }

        if (outputBuffer != null)
        {
            outputBuffer.Release();
            outputBuffer = null;
        }

        // �������
        foreach (var buffer in bufferPool.Values)
        {
            if (buffer != null)
            {
                buffer.Release();
            }
        }
        bufferPool.Clear();

        // ������Ⱦ�����
        foreach (var texture in rtPool.Values)
        {
            if (texture != null)
            {
                texture.Release();
                UnityEngine.Object.Destroy(texture);
            }
        }
        rtPool.Clear();

        isDisposed = true;

        if (debugLogging)
        {
            Debug.Log("GPUInferenceManager: ���ͷ�������Դ");
        }
    }

    // �ڶ�����������ʱȷ���ͷ���Դ
    ~GPUInferenceManager()
    {
        if (!isDisposed)
        {
            Debug.LogWarning("GPUInferenceManager: ��Դδͨ��Dispose������ȷ�ͷţ����ս�����ǿ������");
            Dispose();
        }
    }
}