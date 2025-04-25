using UnityEngine;

public static class ComputeShaderUtils
{
    private static bool debugLogging = false;

    /// <summary>
    /// ���ȼ�����ɫ�����Զ������߳����С
    /// </summary>
    /// <param name="shader">������ɫ��</param>
    /// <param name="kernelIndex">�ں�����</param>
    /// <param name="width">������</param>
    /// <param name="height">����߶�</param>
    public static void DispatchComputeShader(ComputeShader shader, int kernelIndex, int width, int height)
    {
        if (shader == null)
        {
            Debug.LogError("DispatchComputeShader: ������ɫ��Ϊ��");
            return;
        }

        if (width <= 0 || height <= 0)
        {
            Debug.LogError($"DispatchComputeShader: ��Ч�ĳߴ� {width}x{height}");
            return;
        }

        try
        {
            // ��ȡ�߳����С
            uint threadGroupSizeX, threadGroupSizeY, threadGroupSizeZ;
            shader.GetKernelThreadGroupSizes(kernelIndex, out threadGroupSizeX, out threadGroupSizeY, out threadGroupSizeZ);

            // ȷ���߳����С����
            if (threadGroupSizeX == 0 || threadGroupSizeY == 0)
            {
                Debug.LogError($"������ɫ���߳����С��Ч: [{threadGroupSizeX}, {threadGroupSizeY}, {threadGroupSizeZ}]");
                return;
            }

            // ������ȴ�С
            int dispatchX = Mathf.CeilToInt(width / (float)threadGroupSizeX);
            int dispatchY = Mathf.CeilToInt(height / (float)threadGroupSizeY);

            // ȷ�����ȴ�С����Ϊ1
            dispatchX = Mathf.Max(1, dispatchX);
            dispatchY = Mathf.Max(1, dispatchY);

            if (debugLogging)
            {
                Debug.Log($"���ȼ�����ɫ��: �ߴ�={width}x{height}, �߳���=[{dispatchX},{dispatchY},1], �̴߳�С=[{threadGroupSizeX},{threadGroupSizeY},{threadGroupSizeZ}]");
            }

            // ִ�е���
            shader.Dispatch(kernelIndex, dispatchX, dispatchY, 1);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"���ȼ�����ɫ��ʱ�����쳣: {e.Message}");
        }
    }

    /// <summary>
    /// ���ȼ�����ɫ��������ָ���߳����С
    /// </summary>
    public static void DispatchComputeShaderCustom(ComputeShader shader, int kernelIndex, int groupsX, int groupsY, int groupsZ = 1)
    {
        if (shader == null)
        {
            Debug.LogError("DispatchComputeShaderCustom: ������ɫ��Ϊ��");
            return;
        }

        try
        {
            // ȷ�����ȴ�С����Ϊ1
            groupsX = Mathf.Max(1, groupsX);
            groupsY = Mathf.Max(1, groupsY);
            groupsZ = Mathf.Max(1, groupsZ);

            // ִ�е���
            shader.Dispatch(kernelIndex, groupsX, groupsY, groupsZ);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"���ȼ�����ɫ��ʱ�����쳣: {e.Message}");
        }
    }

    /// <summary>
    /// ��������ɫ���Ƿ����
    /// </summary>
    public static bool IsComputeShaderSupported()
    {
        return SystemInfo.supportsComputeShaders;
    }

    /// <summary>
    /// ��ȡ������ɫ������߳����С
    /// </summary>
    public static Vector3Int GetMaxThreadGroupSize()
    {
        int maxComputeWorkGroupSize = SystemInfo.maxComputeWorkGroupSize;
        return new Vector3Int(maxComputeWorkGroupSize, maxComputeWorkGroupSize, maxComputeWorkGroupSize);
    }
}