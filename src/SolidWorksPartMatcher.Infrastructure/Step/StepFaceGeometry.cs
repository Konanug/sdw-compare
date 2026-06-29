namespace SolidWorksPartMatcher.Infrastructure.Step;

/// <summary>Geometric data for a single ADVANCED_FACE, ready for rendering or diffing.</summary>
public sealed record StepFaceGeometry(
    int SurfaceId,
    string SurfaceType,                      // "CYLINDRICAL_SURFACE", "PLANE", "CONICAL_SURFACE", etc.
    string Descriptor,                       // canonical face descriptor string (for match comparison)
    IReadOnlyList<double[]> BoundaryPoints,  // vertex positions in metres
    double[]? AxisOrigin,                    // for cylinders/cones: point on axis (metres)
    double[]? AxisDirection,                 // for cylinders/cones/planes: axis/normal unit vector
    double? Radius);                         // for cylinders/spheres: radius in metres
