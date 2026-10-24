using GRC.Helpers;
using Xunit;

namespace GRC.Tests;

public class VectorMathHelperTests
{
    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        float[] v1 = [1.0f, 2.0f, 3.0f];
        float[] v2 = [1.0f, 2.0f, 3.0f];

        float similarity = VectorMathHelper.CosineSimilarity(v1, v2);

        Assert.Equal(1.0f, similarity, precision: 4);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        float[] v1 = [1.0f, 0.0f];
        float[] v2 = [0.0f, 1.0f];

        float similarity = VectorMathHelper.CosineSimilarity(v1, v2);

        Assert.Equal(0.0f, similarity, precision: 4);
    }

    [Fact]
    public void CosineSimilarity_OppositeVectors_ReturnsNegativeOne()
    {
        float[] v1 = [1.0f, 2.0f];
        float[] v2 = [-1.0f, -2.0f];

        float similarity = VectorMathHelper.CosineSimilarity(v1, v2);

        Assert.Equal(-1.0f, similarity, precision: 4);
    }

    [Fact]
    public void CosineSimilarity_DifferentLengths_ReturnsZero()
    {
        float[] v1 = [1.0f, 2.0f, 3.0f];
        float[] v2 = [1.0f, 2.0f];

        float similarity = VectorMathHelper.CosineSimilarity(v1, v2);

        Assert.Equal(0.0f, similarity);
    }

    [Fact]
    public void CosineSimilarity_EmptyVectors_ReturnsZero()
    {
        float[] v1 = [];
        float[] v2 = [];

        float similarity = VectorMathHelper.CosineSimilarity(v1, v2);

        Assert.Equal(0.0f, similarity);
    }

    [Fact]
    public void CosineSimilarity_ZeroVector_ReturnsZeroInsteadOfNaN()
    {
        float[] v1 = [0.0f, 0.0f, 0.0f];
        float[] v2 = [1.0f, 2.0f, 3.0f];

        float similarity = VectorMathHelper.CosineSimilarity(v1, v2);

        Assert.False(float.IsNaN(similarity), "Zero-vector similarity must not be NaN.");
        Assert.Equal(0.0f, similarity);
    }

    [Fact]
    public void CosineSimilarity_HighDimensionalVector_ComputesCorrectly()
    {
        // 768차원 gemini-embedding-001 (MRL) 모사
        float[] v1 = new float[768];
        float[] v2 = new float[768];
        for (int i = 0; i < 768; i++)
        {
            v1[i] = (float)Math.Sin(i);
            v2[i] = (float)Math.Sin(i);
        }

        float similarity = VectorMathHelper.CosineSimilarity(v1, v2);

        Assert.Equal(1.0f, similarity, precision: 4);
    }
}
