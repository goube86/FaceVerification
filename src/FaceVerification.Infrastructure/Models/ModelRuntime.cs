using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;

namespace FaceVerification.Infrastructure.Models;

public sealed record ModelManifest(
    string Name, string Version, string File, string Sha256, string License,
    string Source, int InputWidth, int InputHeight, string ColorOrder, int EmbeddingSize);

public sealed class ModelOptions
{
    public const string SectionName = "Models";
    public string RootPath { get; init; } = "models";
    public string YuNetManifest { get; init; } = "yunet/manifest.json";
    public string SFaceManifest { get; init; } = "sface/manifest.json";
}

public sealed class ModelRuntime : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };
    public ModelManifest YuNetManifest { get; }
    public ModelManifest SFaceManifest { get; }
    public InferenceSession YuNetSession { get; }
    public InferenceSession SFaceSession { get; }

    public ModelRuntime(ModelOptions options)
    {
        var root = Path.GetFullPath(options.RootPath, AppContext.BaseDirectory);
        (YuNetManifest, YuNetSession) = Load(root, options.YuNetManifest);
        (SFaceManifest, SFaceSession) = Load(root, options.SFaceManifest);
        WarmUp(YuNetSession, YuNetManifest);
        WarmUp(SFaceSession, SFaceManifest);
    }

    public static ModelManifest ReadAndValidate(string root, string relativeManifest)
    {
        var manifestPath = Path.GetFullPath(Path.Combine(root, relativeManifest));
        if (!manifestPath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Model manifest path escapes the configured root.");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Model manifest was not found.", manifestPath);
        var manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(manifestPath), SerializerOptions)
            ?? throw new InvalidDataException($"Invalid model manifest: {manifestPath}");
        var modelPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, manifest.File);
        if (!File.Exists(modelPath)) throw new FileNotFoundException($"Model '{manifest.Name}' was not found.", modelPath);
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath)));
        if (!actual.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 mismatch for model '{manifest.Name}'. Expected {manifest.Sha256}, got {actual}.");
        return manifest;
    }

    private static (ModelManifest Manifest, InferenceSession Session) Load(string root, string relativeManifest)
    {
        var manifest = ReadAndValidate(root, relativeManifest);
        var modelPath = Path.Combine(root, Path.GetDirectoryName(relativeManifest)!, manifest.File);
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        return (manifest, new InferenceSession(modelPath, sessionOptions));
    }

    private static void WarmUp(InferenceSession session, ModelManifest manifest)
    {
        var input = session.InputMetadata.First();
        var dimensions = input.Value.Dimensions.Select((value, index) => value > 0 ? value : index switch
        {
            0 => 1, 1 => 3, 2 => manifest.InputHeight, 3 => manifest.InputWidth, _ => 1
        }).Select(Convert.ToInt64).ToArray();
        using var tensor = OrtValue.CreateTensorValueFromMemory(new float[dimensions.Aggregate(1L, (a, b) => a * b)], dimensions);
        var inputs = new Dictionary<string, OrtValue> { [input.Key] = tensor };
        using var outputs = session.Run(new RunOptions(), inputs, session.OutputNames);
    }

    public void Dispose()
    {
        YuNetSession.Dispose();
        SFaceSession.Dispose();
    }
}
