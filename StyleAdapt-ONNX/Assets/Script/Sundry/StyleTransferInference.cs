using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using UnityEngine;
using UnityEngine.Rendering;

public class StyleTransferInference : MonoBehaviour
{
    private InferenceSession _session;
    private const string ModelPathInResources = "Models/SourceAdaConv"; // ����Ҫ��չ��
    private const int InputSize = 256;
    private Tensor<float> contentInput;
    private Tensor<float> styleInput;

    void Start()
    {
        InitializeInferenceEnvironment();
        contentInput = CreateZeroTensor();
        styleInput = CreateZeroTensor();
    }

    private void InitializeInferenceEnvironment()
    {
        try
        {
            LogSystemInfo();
            LogCudaEnvironmentDetails();

            bool isGpuAvailable = DetectGpuAvailability();

            var options = new SessionOptions();

            if (isGpuAvailable)
            {
                try
                {
                    options.AppendExecutionProvider_CUDA(0);
                    Debug.Log("�ɹ�����CUDA GPU����");
                }
                catch (Exception cudaEx)
                {
                    Debug.LogWarning($"CUDA��ʼ��ʧ�ܣ������˵�CPU: {cudaEx.Message}");
                    options.AppendExecutionProvider_CPU();
                }
            }
            else
            {
                Debug.Log("δ��⵽����GPU����ʹ��CPU����");
                options.AppendExecutionProvider_CPU();
            }

            // ��ȡģ���ļ���ʵ��·��
            string modelPath = GetModelFilePath();
            if (!File.Exists(modelPath))
            {
                throw new Exception($"ģ���ļ�������: {modelPath}");
            }

            // ��������Ự
            _session = new InferenceSession(modelPath, options);
        }
        catch (Exception ex)
        {
            Debug.LogError($"��������ʼ��ʧ��: {ex.Message}");
            Debug.LogError($"��ϸ�쳣��Ϣ: {ex.StackTrace}");
        }
    }

    private string GetModelFilePath()
    {
        // ��ȡ Unity ��Ŀ�� Resources �ļ��е�ʵ��·��
        string resourcesPath = Path.Combine(Application.dataPath, "Resources");
        return Path.Combine(resourcesPath, $"{ModelPathInResources}.onnx");
    }

    private bool DetectGpuAvailability()
    {
        try
        {
            // 1. ����Ƿ�NVIDIA�Կ�
            if (!SystemInfo.graphicsDeviceName.Contains("NVIDIA"))
            {
                Debug.LogWarning($"��NVIDIA�Կ�: {SystemInfo.graphicsDeviceName}");
                return false;
            }

            // 2. �ſ�ͼ��API�������
            var unsupportedApis = new[]
            {
                GraphicsDeviceType.Metal,
                GraphicsDeviceType.OpenGLES2,
                GraphicsDeviceType.OpenGLES3
            };

            if (unsupportedApis.Contains(SystemInfo.graphicsDeviceType))
            {
                Debug.LogWarning($"��֧�ֵ�ͼ��API: {SystemInfo.graphicsDeviceType}");
                return false;
            }

            // 3. ���CUDA����
            string cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (string.IsNullOrEmpty(cudaPath))
            {
                Debug.Log("CUDA_PATH��������δ����");
                return false;
            }

            // 4. ��֤CUDA 12.x���ļ�
            var requiredLibs = new Dictionary<string, string>
            {
                { "cudart64_12.dll", "CUDA 12����ʱ��" },
                { "cublas64_12.dll", "CUDA�������Դ�����" },
                { "cudnn_ops64_9.dll", "cuDNN�����" }
            };

            foreach (var lib in requiredLibs)
            {
                string libPath = Path.Combine(cudaPath, "bin", lib.Key);
                if (!File.Exists(libPath))
                {
                    Debug.LogWarning($"ȱʧ�ؼ���: {lib.Value}\n·��: {libPath}");
                    return false;
                }
            }

            // 5. ʵ�ʼ��ز���
            return CheckCudaRuntimeLoadable();
        }
        catch (Exception ex)
        {
            Debug.LogError($"GPU����쳣: {ex.Message}");
            return false;
        }
    }

    // CUDA����ʱ������֤
    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    private bool CheckCudaRuntimeLoadable()
    {
        try
        {
            // ���Լ��ض���汾�� CUDA ����ʱ��
            string[] cudaVersions = {
                "cudart64_12.dll",
                "cudart64_11.dll",
                "cudart64_10.dll"
            };

            foreach (var cudaDll in cudaVersions)
            {
                string fullPath = Path.Combine(Environment.GetEnvironmentVariable("CUDA_PATH"), "bin", cudaDll);
                IntPtr handle = LoadLibrary(fullPath);

                if (handle != IntPtr.Zero)
                {
                    Debug.Log($"�ɹ����� CUDA ����ʱ��: {cudaDll}");
                    return true;
                }
            }

            Debug.LogError("δ�ܼ����κ� CUDA ����ʱ��");
            return false;
        }
        catch (Exception ex)
        {
            Debug.LogError($"��� CUDA ����ʱʱ�����쳣: {ex.Message}");
            return false;
        }
    }

    private void LogSystemInfo()
    {
        Debug.Log($"����ϵͳ: {SystemInfo.operatingSystem}");
        Debug.Log($"������: {SystemInfo.processorType}");
        Debug.Log($"�Կ�����: {SystemInfo.graphicsDeviceName}");
        Debug.Log($"�Կ�����: {SystemInfo.graphicsDeviceType}");
        Debug.Log($"�Դ��С: {SystemInfo.graphicsMemorySize} MB");
    }

    private void LogCudaEnvironmentDetails()
    {
        // ��ӡ CUDA ��ػ�������
        string[] cudaEnvVars = {
            "CUDA_PATH",
            "PATH",
            "LD_LIBRARY_PATH"
        };

        foreach (var envVar in cudaEnvVars)
        {
            string value = Environment.GetEnvironmentVariable(envVar);
            Debug.Log($"{envVar}: {value ?? "δ����"}");
        }

        // ��� CUDA ���ļ��Ƿ����
        string cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrEmpty(cudaPath))
        {
            string binPath = Path.Combine(cudaPath, "bin");
            string[] libsToCheck = {
                "cudart64_12.dll",
                "cublas64_12.dll",
                "cudnn_ops64_9.dll"
            };

            foreach (var lib in libsToCheck)
            {
                string fullPath = Path.Combine(binPath, lib);
                Debug.Log($"�����ļ� {lib}: {(File.Exists(fullPath) ? "����" : "������")}");
            }
        }
    }

    void Update()
    {
        try
        {
            var sw = new System.Diagnostics.Stopwatch();
            sw.Start();

            var output = RunInference(contentInput, styleInput);

            sw.Stop();
            Debug.Log($"��������ʱ: {sw.ElapsedMilliseconds} ms");
            Debug.Log($"�����������: {output.Length}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"�������ʧ��: {ex.Message}");
            Debug.LogError($"��ϸ�쳣��Ϣ: {ex.StackTrace}");
        }
    }

    private Tensor<float> CreateZeroTensor()
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        return tensor;
    }

    private float[] RunInference(Tensor<float> contentInput, Tensor<float> styleInput)
    {
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("content", contentInput),
            NamedOnnxValue.CreateFromTensor("style", styleInput)
        };

        using (var results = _session.Run(inputs))
        {
            return results.First().AsTensor<float>().ToArray();
        }
    }

    void OnDestroy()
    {
        _session?.Dispose();
    }
}