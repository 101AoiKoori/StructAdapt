using UnityEngine;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Collections.Generic;
using System.Linq;

public static class TextureUtils
{
    private static ComputeShader textureConversionShader;
    private static int textureToTensorKernel;
    private static int tensorToTextureKernel;
    private static bool debugLogging = false;

    // �������� - ���ⷴ�������ͷ�ComputeBuffer
    private static Dictionary<int, ComputeBuffer> bufferPool = new Dictionary<int, ComputeBuffer>();

    // ��ʱ��Ⱦ����� - ����Ƶ������RenderTexture
    private static Dictionary<string, RenderTexture> rtPool = new Dictionary<string, RenderTexture>();

    static TextureUtils()
    {
        textureConversionShader = Resources.Load<ComputeShader>("Shaders/TextureConversion");
        if (textureConversionShader == null)
        {
            Debug.LogError("�޷���������ת��������ɫ������ȷ������Resources/ShadersĿ¼��");
            return;
        }

        textureToTensorKernel = textureConversionShader.FindKernel("CSTextureToTensor");
        tensorToTextureKernel = textureConversionShader.FindKernel("CSTensorToTexture");

        if (debugLogging)
        {
            Debug.Log("TextureUtils ��ʼ���ɹ�");
        }
    }

    /// <summary>
    /// �ӻ������ػ�ȡ�򴴽�ComputeBuffer
    /// </summary>
    private static ComputeBuffer GetOrCreateBuffer(int size, int stride)
    {
        int key = size * 1000 + stride; // �򵥵Ĺ�ϣ��
        if (bufferPool.TryGetValue(key, out ComputeBuffer buffer))
        {
            if (buffer != null && buffer.count == size && buffer.stride == stride)
            {
                return buffer;
            }
            // �����С��ƥ�䣬�ͷžɵ�
            if (buffer != null)
            {
                buffer.Release();
            }
        }

        // �����µĻ�����
        buffer = new ComputeBuffer(size, stride);
        bufferPool[key] = buffer;
        return buffer;
    }

    /// <summary>
    /// ����Ⱦ����ػ�ȡ�򴴽�RenderTexture
    /// </summary>
    private static RenderTexture GetOrCreateRenderTexture(int width, int height, RenderTextureFormat format)
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
                Object.Destroy(rt);
            }
        }

        // �����µ���Ⱦ����
        rt = new RenderTexture(width, height, 0, format);
        rt.enableRandomWrite = true;
        rt.Create();
        rtPool[key] = rt;
        return rt;
    }

    public static Tensor<float> TextureToTensor(Texture texture, int channels, int targetWidth, int targetHeight, int batchSize)
    {
        if (texture == null || textureConversionShader == null)
        {
            Debug.LogError("����������ɫ����Ч");
            return null;
        }

        try
        {
            // ��ȡ��ʱ��Ⱦ����
            RenderTexture tempRT = GetOrCreateRenderTexture(targetWidth, targetHeight, RenderTextureFormat.ARGBFloat);

            // �������������ŵ�Ŀ��ߴ�
            Graphics.Blit(texture, tempRT);

            // ��ȡ�㹻��Ļ�����
            int bufferSize = batchSize * channels * targetWidth * targetHeight;
            ComputeBuffer tensorBuffer = GetOrCreateBuffer(bufferSize, sizeof(float));

            // ���ü�����ɫ������
            textureConversionShader.SetTexture(textureToTensorKernel, "_InputTexture", tempRT);
            textureConversionShader.SetBuffer(textureToTensorKernel, "_OutputBuffer", tensorBuffer);
            textureConversionShader.SetInt("_Width", targetWidth);
            textureConversionShader.SetInt("_Height", targetHeight);
            textureConversionShader.SetInt("_Channels", channels);
            textureConversionShader.SetInt("_BatchSize", batchSize);

            // �Ż��߳����С - 16�Ǽ�����ɫ���е��߳���ߴ�
            int threadGroupsX = Mathf.CeilToInt(targetWidth / 16f);
            int threadGroupsY = Mathf.CeilToInt(targetHeight / 16f);
            ComputeShaderUtils.DispatchComputeShaderCustom(textureConversionShader, textureToTensorKernel, threadGroupsX, threadGroupsY);

            // ��ȡ�������
            float[] tensorData = new float[bufferSize];
            tensorBuffer.GetData(tensorData);

            // ����ONNX����ʱ��Ҫ��DenseTensor
            var tensor = new DenseTensor<float>(new[] { batchSize, channels, targetHeight, targetWidth });

            // ����������� - �Ż�����������
            for (int n = 0; n < batchSize; n++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int channelOffset = c * targetWidth * targetHeight;
                    for (int h = 0; h < targetHeight; h++)
                    {
                        int rowOffset = h * targetWidth;
                        for (int w = 0; w < targetWidth; w++)
                        {
                            int index = n * channels * targetWidth * targetHeight + channelOffset + rowOffset + w;
                            tensor[n, c, h, w] = tensorData[index];
                        }
                    }
                }
            }

            return tensor;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"TextureToTensor����: {e.Message}");
            return null;
        }
    }

    public static void TensorToRenderTexture(Tensor<float> tensor, RenderTexture target, Vector4 colorBias, int targetWidth, int targetHeight)
    {
        if (tensor == null || target == null || textureConversionShader == null)
        {
            Debug.LogError("������Ŀ������������ɫ����Ч");
            return;
        }

        try
        {
            // ȷ��Ŀ������ߴ���ȷ
            if (target.width != targetWidth || target.height != targetHeight)
            {
                Debug.LogWarning($"Ŀ������ߴ�({target.width}x{target.height})�������ߴ�({targetWidth}x{targetHeight})��ƥ��");
                // �Լ���ִ�У���Ϊ���ݻᱻ����
            }

            // ����������С
            int tensorSize = 1;
            foreach (var dim in tensor.Dimensions)
            {
                tensorSize *= dim;
            }

            // ��ȡ���㻺����
            ComputeBuffer tensorBuffer = GetOrCreateBuffer(tensorSize, sizeof(float));
            tensorBuffer.SetData(tensor.ToArray());

            // ȷ��Ŀ�������������д��
            if (!target.enableRandomWrite)
            {
                target.enableRandomWrite = true;
                target.Create();
            }

            // ���ü�����ɫ������
            textureConversionShader.SetBuffer(tensorToTextureKernel, "_InputBuffer", tensorBuffer);
            textureConversionShader.SetTexture(tensorToTextureKernel, "_OutputTexture", target);
            textureConversionShader.SetVector("_ColorBias", colorBias);
            textureConversionShader.SetInt("_Width", targetWidth);
            textureConversionShader.SetInt("_Height", targetHeight);
            textureConversionShader.SetInt("_Channels", tensor.Dimensions[1]);

            // �Ż��߳����С
            int threadGroupsX = Mathf.CeilToInt(targetWidth / 16f);
            int threadGroupsY = Mathf.CeilToInt(targetHeight / 16f);
            ComputeShaderUtils.DispatchComputeShaderCustom(textureConversionShader, tensorToTextureKernel, threadGroupsX, threadGroupsY);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"TensorToRenderTexture����: {e.Message}");
        }
    }

    /// <summary>
    /// �������л���������Ⱦ����
    /// </summary>
    public static void Cleanup()
    {
        // �ͷ����л�����
        foreach (var buffer in bufferPool.Values)
        {
            if (buffer != null)
            {
                buffer.Release();
            }
        }
        bufferPool.Clear();

        // �ͷ�������Ⱦ����
        foreach (var rt in rtPool.Values)
        {
            if (rt != null)
            {
                rt.Release();
                Object.Destroy(rt);
            }
        }
        rtPool.Clear();
    }
}