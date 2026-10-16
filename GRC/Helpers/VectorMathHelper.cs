namespace GRC.Helpers;

using System;
using System.Numerics.Tensors;

/// <summary>
/// .NET 8.0 TensorPrimitives 기반 하드웨어 가속(SIMD) 벡터 연산 헬퍼
/// </summary>
public static class VectorMathHelper
{
    /// <summary>
    /// 두 벡터 간의 코사인 유사도를 하드웨어 SIMD(TensorPrimitives)를 통해 고속 계산합니다.
    /// 길이가 다르거나 비어있거나 영벡터(Zero-Vector)로 인해 NaN이 발생하는 경우 0.0f를 반환합니다.
    /// </summary>
    /// <param name="x">첫 번째 벡터 스팬</param>
    /// <param name="y">두 번째 벡터 스팬</param>
    /// <returns>코사인 유사도 (-1.0f ~ 1.0f, 유효하지 않은 경우 0.0f)</returns>
    public static float CosineSimilarity(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        if (x.IsEmpty || y.IsEmpty || x.Length != y.Length)
        {
            return 0.0f;
        }

        float similarity = TensorPrimitives.CosineSimilarity(x, y);
        return float.IsNaN(similarity) ? 0.0f : similarity;
    }
}
