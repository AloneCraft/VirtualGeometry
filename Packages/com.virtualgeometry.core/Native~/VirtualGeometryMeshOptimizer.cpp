#include "meshoptimizer.h"

#include <cstdint>
#include <limits>

#if defined(_WIN32)
#define VG_API extern "C" __declspec(dllexport)
#else
#define VG_API extern "C" __attribute__((visibility("default")))
#endif

namespace
{
    constexpr int InvalidArgument = -1;
    constexpr int ResultTooLarge = -2;
}

VG_API int vgMeshoptVersion()
{
    return MESHOPTIMIZER_VERSION;
}

VG_API int vgMeshoptSimplifyWithAttributes(
    std::uint32_t* destination,
    std::uint32_t destinationCapacity,
    const std::uint32_t* indices,
    std::uint32_t indexCount,
    const float* vertexPositions,
    std::uint32_t vertexCount,
    std::uint32_t vertexPositionsStride,
    const float* vertexAttributes,
    std::uint32_t vertexAttributesStride,
    const float* attributeWeights,
    std::uint32_t attributeCount,
    const std::uint8_t* vertexLock,
    std::uint32_t targetIndexCount,
    float targetError,
    std::uint32_t options,
    float* resultError)
{
    if(destination == nullptr || indices == nullptr || vertexPositions == nullptr || resultError == nullptr ||
       destinationCapacity < indexCount || indexCount == 0 || indexCount % 3 != 0 || vertexCount == 0 ||
       vertexPositionsStride < sizeof(float) * 3 || vertexPositionsStride % sizeof(float) != 0 ||
       targetIndexCount < 3 || targetIndexCount > indexCount || targetIndexCount % 3 != 0 ||
       attributeCount > 32 ||
       (attributeCount > 0 && (vertexAttributes == nullptr || attributeWeights == nullptr ||
        vertexAttributesStride < attributeCount * sizeof(float) || vertexAttributesStride % sizeof(float) != 0)))
    {
        return InvalidArgument;
    }

    const std::size_t result = meshopt_simplifyWithAttributes(
        destination,
        indices,
        indexCount,
        vertexPositions,
        vertexCount,
        vertexPositionsStride,
        vertexAttributes,
        vertexAttributesStride,
        attributeWeights,
        attributeCount,
        vertexLock,
        targetIndexCount,
        targetError,
        options,
        resultError);

    if(result > static_cast<std::size_t>(std::numeric_limits<int>::max()))
    {
        return ResultTooLarge;
    }

    return static_cast<int>(result);
}

