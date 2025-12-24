using UnityEngine;
using UnityEngine.Rendering;
using Plugin = Klak.Syphon.Interop.PluginServer;

namespace Klak.Syphon {

[ExecuteInEditMode]
public sealed class SyphonServer : MonoBehaviour
{
    #region Watermark settings

    public enum WatermarkAnchor
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    [field:SerializeField] public bool WatermarkEnabled { get; set; }

    [field:SerializeField] public Sprite WatermarkSprite { get; set; }

    [field:SerializeField]
    public WatermarkAnchor WatermarkAnchorMode { get; set; } = WatermarkAnchor.BottomRight;

    [field:SerializeField]
    public Vector2 WatermarkOffset { get; set; } = new Vector2(32, 32);

    [field:SerializeField] public float WatermarkScale { get; set; } = 1f;

    static readonly Color s_watermarkNeutralColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);

    void ApplyWatermarkIfRequested()
    {
        if (!WatermarkEnabled) return;
        if (WatermarkSprite == null) return;
        if (_plugin.texture == null) return;
        if (WatermarkScale <= 0) return;

        var watermarkTexture = WatermarkSprite.texture;
        if (watermarkTexture == null) return;

        var destinationWidth = _plugin.texture.width;
        var destinationHeight = _plugin.texture.height;
        if (destinationWidth <= 0 || destinationHeight <= 0) return;

        var spriteTextureRect = WatermarkSprite.textureRect;
        if (spriteTextureRect.width <= 0 || spriteTextureRect.height <= 0) return;

        var watermarkWidth = spriteTextureRect.width * WatermarkScale;
        var watermarkHeight = spriteTextureRect.height * WatermarkScale;

        if (watermarkWidth <= 0 || watermarkHeight <= 0) return;

        if (watermarkWidth > destinationWidth || watermarkHeight > destinationHeight)
        {
            var widthScale = destinationWidth / watermarkWidth;
            var heightScale = destinationHeight / watermarkHeight;
            var clampedScale = Mathf.Min(widthScale, heightScale);
            watermarkWidth *= clampedScale;
            watermarkHeight *= clampedScale;
        }

        var destinationRect = CalculateWatermarkRect(
            destinationWidth, destinationHeight,
            watermarkWidth, watermarkHeight,
            WatermarkAnchorMode, WatermarkOffset
        );

        var sourceRect = new Rect(
            spriteTextureRect.x / watermarkTexture.width,
            spriteTextureRect.y / watermarkTexture.height,
            spriteTextureRect.width / watermarkTexture.width,
            spriteTextureRect.height / watermarkTexture.height
        );

        var tempRenderTexture = RenderTexture.GetTemporary(destinationWidth, destinationHeight, 0);

        Graphics.Blit(_plugin.texture, tempRenderTexture);

        var previousActive = RenderTexture.active;
        RenderTexture.active = tempRenderTexture;

        GL.PushMatrix();
        GL.LoadPixelMatrix(0, destinationWidth, 0, destinationHeight);

        Graphics.DrawTexture(
            destinationRect,
            watermarkTexture,
            sourceRect,
            0, 0, 0, 0,
            s_watermarkNeutralColor
        );

        GL.PopMatrix();
        RenderTexture.active = previousActive;

        Blitter.Blit(tempRenderTexture, _plugin.texture, KeepAlpha);
        RenderTexture.ReleaseTemporary(tempRenderTexture);
    }

    static Rect CalculateWatermarkRect(
        int destinationWidth,
        int destinationHeight,
        float watermarkWidth,
        float watermarkHeight,
        WatermarkAnchor watermarkAnchor,
        Vector2 watermarkOffset
    )
    {
        var positionX = watermarkOffset.x;
        var positionY = watermarkOffset.y;

        switch (watermarkAnchor)
        {
            case WatermarkAnchor.TopLeft:
                positionX = watermarkOffset.x;
                positionY = watermarkOffset.y;
                break;

            case WatermarkAnchor.TopRight:
                positionX = destinationWidth - watermarkOffset.x - watermarkWidth;
                positionY = watermarkOffset.y;
                break;

            case WatermarkAnchor.BottomLeft:
                positionX = watermarkOffset.x;
                positionY = destinationHeight - watermarkOffset.y - watermarkHeight;
                break;

            case WatermarkAnchor.BottomRight:
                positionX = destinationWidth - watermarkOffset.x - watermarkWidth;
                positionY = destinationHeight - watermarkOffset.y - watermarkHeight;
                break;
        }

        positionX = Mathf.Clamp(positionX, 0, destinationWidth - watermarkWidth);
        positionY = Mathf.Clamp(positionY, 0, destinationHeight - watermarkHeight);

        return new Rect(positionX, positionY, watermarkWidth, watermarkHeight);
    }

    #endregion
    
    #region Public properties

    public string ServerName
      { get => _serverName;
        set { TeardownPlugin(); _serverName = value; } }

    public CaptureMethod CaptureMethod
      { get => _captureMethod;
        set { TeardownPlugin(); _captureMethod = value; } }

    public Camera SourceCamera
      { get => _sourceCamera;
        set { TeardownPlugin(); _sourceCamera = value; } }

    public Texture SourceTexture
      { get => _sourceTexture;
        set { TeardownPlugin(); _sourceTexture = value; } }

    [field:SerializeField] public bool KeepAlpha { get; set; }

    [field:SerializeField] public SyphonResources Resources { get; set; }

    #endregion

    #region Property backing fields

    [SerializeField] string _serverName = "Syphon Server";
    [SerializeField] CaptureMethod _captureMethod;
    [SerializeField] Camera _sourceCamera;
    [SerializeField] Texture _sourceTexture;

    #endregion

    #region SRP callback

    Camera _attachedCamera;

    #if KLAK_SYPHON_HAS_SRP

    void AttachCameraCallback(Camera target)
    {
        CameraCaptureBridge.AddCaptureAction(target, OnCameraCapture);
        _attachedCamera = target;
    }

    void ResetCameraCallback()
    {
        if (_attachedCamera == null) return;
        CameraCaptureBridge.RemoveCaptureAction(_attachedCamera, OnCameraCapture);
        _attachedCamera = null;
    }

    void OnCameraCapture(RenderTargetIdentifier source, CommandBuffer cb)
    {
        if (_attachedCamera == null || _plugin.texture == null) return;
        Blitter.Blit(cb, source, _plugin.texture, KeepAlpha);
    }

    #else

    void AttachCameraCallback(Camera target) {}
    void ResetCameraCallback() {}

    #endif

    #endregion

    #region Syphon server plugin
 
    (Plugin instance, Texture2D texture) _plugin;

    void SetupPlugin()
    {
        if (_plugin.instance != null) return;

        // Server name validity
        if (string.IsNullOrEmpty(_serverName)) return;

        // Texture capture mode
        if (_captureMethod == CaptureMethod.Texture)
        {
            if (_sourceTexture == null) return;
            var (w, h) = (_sourceTexture.width, _sourceTexture.height);
            _plugin = Plugin.CreateWithBackedTexture(_serverName, w, h);
        }

        // Camera capture mode
        if (_captureMethod == CaptureMethod.Camera)
        {
            if (_sourceCamera == null) return;
            var (w, h) = (_sourceCamera.pixelWidth, _sourceCamera.pixelHeight);
            _plugin = Plugin.CreateWithBackedTexture(_serverName, w, h);
            AttachCameraCallback( _sourceCamera);
        }

        // Game View capture mode
        if (_captureMethod == CaptureMethod.GameView)
        {
            var (w, h) = (Screen.width, Screen.height);
            _plugin = Plugin.CreateWithBackedTexture(_serverName, w, h);
        }

        // Blitter lazy initialization
        Blitter.Prepare(Resources);

        // Coroutine start
        StartCoroutine(CaptureCoroutine());
    }

    void TeardownPlugin()
    {
        // Plugin instance/texture disposal
        _plugin.instance?.Dispose();
        Utility.Destroy(_plugin.texture);
        _plugin = (null, null);

        ResetCameraCallback();
        StopAllCoroutines();
    }

    #endregion

    #region Capture coroutine

    System.Collections.IEnumerator CaptureCoroutine()
    {
        for (var eof = new WaitForEndOfFrame(); true;)
        {
            // End of the frame
            yield return eof;

            if (_plugin.instance == null) continue;

            // Texture capture mode
            if (_captureMethod == CaptureMethod.Texture)
                Blitter.Blit(_sourceTexture, _plugin.texture, KeepAlpha);

            // Game View capture mode
            if (_captureMethod == CaptureMethod.GameView)
            {
                var rt = Utility.CaptureScreenAsTempRT();
                Blitter.Blit(rt, _plugin.texture, KeepAlpha, vflip: true);
                RenderTexture.ReleaseTemporary(rt);
            }
            
            // Optional watermark overlay (output buffer only)
            ApplyWatermarkIfRequested();

            // Frame update notification
            _plugin.instance.PublishTexture();
        }
    }

    #endregion

    #region MonoBehaviour implementation

    void Start()
      => InternalCommon.ApplyCurrentColorSpace();

    void OnValidate()
      => TeardownPlugin();

    void OnDisable()
      => TeardownPlugin();

    void Update()
      => SetupPlugin();

    #endregion
}

} // namespace Klak.Syphon
