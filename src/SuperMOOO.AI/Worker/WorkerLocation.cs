using SuperMOOO.Core;

namespace SuperMOOO.AI.Worker;

public static class WorkerLocation
{
    /// <summary>Thư mục chứa gói aivs_worker và requirements.lock (được chép cạnh app lúc build).</summary>
    public static string Directory(AppPaths paths) => Path.Combine(paths.AppRoot, "ai_worker");

    public static string RequirementsLock(AppPaths paths) => Path.Combine(Directory(paths), "requirements.lock");

    public static string DenoiserModel(AppPaths paths) => Path.Combine(paths.Models, "denoise", "denoiser.onnx");

    /// <summary>Speaker encoder của VieNeu v3 Turbo (embedding giọng clone tiếng Việt).</summary>
    public static string SpeakerModel(AppPaths paths) => Path.Combine(paths.Models, "speaker", "speaker_encoder.onnx");

    /// <summary>MDX-Net của UVR (ONNX): tách giọng gốc, giữ nhạc + hiệu ứng khi lồng tiếng. Tên file quyết định tham số STFT bên worker.</summary>
    public static string SeparationModel(AppPaths paths) => Path.Combine(paths.Models, "separation", "UVR-MDX-NET-Voc_FT.onnx");

    /// <summary>Mel-Band RoFormer vocals của Kim (PyTorch .ckpt): tách giọng chất lượng cao, cần torch trong venv worker.</summary>
    public static string RoformerModel(AppPaths paths) => Path.Combine(paths.Models, "separation", "mel_band_roformer_kim_vocals.ckpt");

    /// <summary>PP-OCRv4 det (ONNX): tìm/dò phụ đề cứng trong video.</summary>
    public static string TextDetModel(AppPaths paths) => Path.Combine(paths.Models, "vision", "text_det.onnx");

    /// <summary>MI-GAN pipeline (ONNX): xoá phụ đề cứng bằng inpaint.</summary>
    public static string InpaintModel(AppPaths paths) => Path.Combine(paths.Models, "vision", "migan.onnx");
}
