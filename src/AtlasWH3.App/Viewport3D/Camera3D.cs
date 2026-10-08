using System.Numerics;

namespace AtlasWH3.App.Viewport3D;

/// <summary>
/// Orbit camera around a target (Terry-style): yaw 0 looks north (+z), pitch &gt; 0 looks down. Left-handed (D3D),
/// world x east / y up / z north. Near and far planes follow the orbit distance so tiny props and the whole campaign
/// map both stay usable.
/// </summary>
public sealed class Camera3D
{
    public Vector3 Target { get; set; }
    public float Distance { get; set; } = 50;
    public float Yaw { get; set; }
    public float Pitch { get; set; } = 0.75f;
    public float FieldOfView { get; set; } = MathF.PI / 3.5f;
    public float Aspect { get; set; } = 1.5f;

    public Vector3 Forward => new(MathF.Sin(Yaw) * MathF.Cos(Pitch), -MathF.Sin(Pitch), MathF.Cos(Yaw) * MathF.Cos(Pitch));
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Vector3.UnitY, Forward));
    public Vector3 Eye => Target - Forward * Distance;

    public float Near => Math.Clamp(Distance * 0.002f, 0.005f, 5f);
    public float Far => Distance * 40 + 2000;

    public Matrix4x4 View => Matrix4x4.CreateLookAtLeftHanded(Eye, Target, Vector3.UnitY);
    public Matrix4x4 Projection => Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(FieldOfView, Aspect, Near, Far);
    public Matrix4x4 ViewProjection => View * Projection;

    public void Orbit(float dYaw, float dPitch)
    {
        Yaw += dYaw;
        Pitch = Math.Clamp(Pitch + dPitch, -1.45f, 1.55f);
    }

    /// <summary>Moves the target in the view plane by a screen-pixel delta (viewport height h).</summary>
    public void Pan(float dx, float dy, float h)
    {
        var worldPerPixel = 2 * Distance * MathF.Tan(FieldOfView / 2) / Math.Max(1, h);
        var up = Vector3.Cross(Forward, Right);
        Target += (-Right * dx + up * dy) * worldPerPixel;
    }

    public void Zoom(float factor) => Distance = Math.Clamp(Distance * factor, 0.05f, 20000);

    /// <summary>Fly (WASD/QE while orbiting): moves the target along the view axes.</summary>
    public void Fly(Vector3 local, float speed)
    {
        var flat = Vector3.Normalize(new Vector3(Forward.X, 0, Forward.Z) + new Vector3(0, 0, 1e-6f));
        Target += (Right * local.X + Vector3.UnitY * local.Y + flat * local.Z) * speed;
    }

    /// <summary>Frames a box.</summary>
    public void Frame(Vector3 min, Vector3 max)
    {
        Target = (min + max) / 2;
        var radius = Math.Max(0.5f, (max - min).Length() / 2);
        Distance = radius / MathF.Sin(FieldOfView / 2) * 1.1f;
    }

    /// <summary>
    /// World ray through a pixel of a viewport of size w × h, built from the camera basis in double precision
    /// (inverting the float view-projection loses centimetres at campaign-map coordinates, enough to throw gizmos off).
    /// </summary>
    public (Vector3 Origin, Vector3 Direction) Ray(float px, float py, float w, float h)
    {
        var (f, r, u) = Basis();
        double x = px / w * 2 - 1, y = 1 - py / h * 2, t = Math.Tan(FieldOfView / 2);
        double dx = f.X + r.X * x * t * Aspect + u.X * y * t;
        double dy = f.Y + r.Y * x * t * Aspect + u.Y * y * t;
        double dz = f.Z + r.Z * x * t * Aspect + u.Z * y * t;
        var len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return (Eye, new Vector3((float)(dx / len), (float)(dy / len), (float)(dz / len)));
    }

    /// <summary>World point → pixel (null when behind the camera); the exact inverse of <see cref="Ray"/>.</summary>
    public Vector2? ToScreen(Vector3 p, float w, float h)
    {
        var (f, r, u) = Basis();
        var e = Eye;
        double rx = p.X - (double)e.X, ry = p.Y - (double)e.Y, rz = p.Z - (double)e.Z;
        var zv = rx * f.X + ry * f.Y + rz * f.Z;
        if (zv <= 1e-9) return null;
        double t = Math.Tan(FieldOfView / 2);
        var xs = (rx * r.X + ry * r.Y + rz * r.Z) / (zv * t * Aspect);
        var ys = (rx * u.X + ry * u.Y + rz * u.Z) / (zv * t);
        return new Vector2((float)((xs + 1) / 2 * w), (float)((1 - ys) / 2 * h));
    }

    /// <summary>Forward, right and up in double precision (left-handed: right = up × forward, up = forward × right).</summary>
    private ((double X, double Y, double Z) F, (double X, double Y, double Z) R, (double X, double Y, double Z) U) Basis()
    {
        double cy = Math.Cos(Yaw), sy = Math.Sin(Yaw), cp = Math.Cos(Pitch), sp = Math.Sin(Pitch);
        var f = (X: sy * cp, Y: -sp, Z: cy * cp);
        var rl = Math.Sqrt(f.Z * f.Z + f.X * f.X);
        var r = (X: f.Z / rl, Y: 0.0, Z: -f.X / rl);                 // UnitY × f, normalised
        var u = (X: f.Y * r.Z - f.Z * r.Y, Y: f.Z * r.X - f.X * r.Z, Z: f.X * r.Y - f.Y * r.X); // f × r
        return (f, r, u);
    }
}
