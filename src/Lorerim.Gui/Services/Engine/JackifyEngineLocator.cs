using System;
using System.IO;

namespace Lorerim.Gui.Services.Engine;

/// <summary>
/// Resolves the bundled jackify-engine binary. Packaged builds carry it in engine/ next to
/// the executable (scripts/setup-deps.sh drops it there); LORERIM_ENGINE_PATH overrides for
/// development against a locally extracted engine.
/// </summary>
public class JackifyEngineLocator
{
    /// <summary>Virtual so tests can pin "no engine" instead of depending on the host layout
    /// and the LORERIM_ENGINE_PATH a developer may have set.</summary>
    public virtual string? EngineDir
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("LORERIM_ENGINE_PATH");
            if (!string.IsNullOrEmpty(overridePath))
            {
                var dir = Directory.Exists(overridePath)
                    ? overridePath
                    : Path.GetDirectoryName(overridePath);
                if (dir is not null && File.Exists(Path.Join(dir, BinaryName)))
                {
                    return dir;
                }
            }
            var bundled = Path.Join(AppContext.BaseDirectory, "engine");
            return File.Exists(Path.Join(bundled, BinaryName)) ? bundled : null;
        }
    }

    public string? EnginePath =>
        EngineDir is { } dir ? Path.Join(dir, BinaryName) : null;

    public const string BinaryName = "jackify-engine";
}
