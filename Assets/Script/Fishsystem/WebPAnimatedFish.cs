using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using unity.libwebp; 
using unity.libwebp.Interop;

public class WebPAnimatedFish : MonoBehaviour
{
    public SpriteRenderer spriteRenderer;

    // 全ての魚のインスタンスを管理するリスト
    private static List<WebPAnimatedFish> activeFish = new List<WebPAnimatedFish>();

    [Header("動き・吸引設定")]
    public float moveSpeed = 2f;
    public float attractionSpeed = 8f;   
    public float attractionRadius = 15f; 
    public float armScanWidth = 2.5f;    
    
    [Header("両手をくっつけた判定")]
    public float handsTogetherThreshold = 3.0f; // ★両手の距離がこれ以下なら開始

    [Header("移動範囲")]
    public float minX = -10f, maxX = 10f, minY = -5f, maxY = 5f;

    private bool isStopped = false;
    private string fishUrl;
    private float lastTransformTime = -30f; 
    private const float TRANSFORM_COOLDOWN = 30f; // ★インターバルを30秒に固定
    private const int REVERT_DELAY_MS = 10000;    // ★QR表示時間は10秒

    private Vector3 targetPosition;
    private List<(Sprite sprite, int time)> frames = new();
    private bool isPlaying = false;

    // 生成時にリストに追加
    void OnEnable() { activeFish.Add(this); }
    // 削除時にリストから除去
    void OnDisable() { activeFish.Remove(this); }

    public async void Initialize(string url)
    {
        this.fishUrl = url;
        SetTarget();
        byte[] bytes = await DownloadWebP(url);
        if (bytes != null) {
            frames = DecodeWebP(bytes);
            if (frames.Count > 0) _ = PlayLoop();
        }
    }

    void Update()
    {
        if (isStopped) return;
        var sse = SseReceiver.Instance;
        if (sse == null) return;

        // 両手の座標が正常に届いているか
        if (sse.rightWrist.x < 500 && sse.leftWrist.x < 500) {
            
            float handDist = Vector2.Distance((Vector2)sse.rightWrist, (Vector2)sse.leftWrist);
            Vector2 handsCenter = ((Vector2)sse.rightWrist + (Vector2)sse.leftWrist) / 2f;

            // 1. 両手がくっついているかチェック
            if (handDist < handsTogetherThreshold) {
                
                // 2. 「一番近い一匹」だけが動く権利を得るロジック
                WebPAnimatedFish closest = GetClosestFish(handsCenter);

                if (closest == this) {
                    // QR変身判定（30秒インターバル）
                    if (Time.time - lastTransformTime > TRANSFORM_COOLDOWN) {
                        if (IsInArmArea2D(sse.rightElbow, sse.rightWrist) || IsInArmArea2D(sse.leftElbow, sse.leftWrist)) {
                            ExecuteTransform();
                            return;
                        }
                    }
                    // 吸引実行（一番近いこいつだけが吸い寄せられる）
                    ApplyAttraction2D(handsCenter);
                }
            }
        }
        MoveFish();
    }

    // 全ての魚の中から、変身中ではなく、かつ最も手に近い一匹を返す
    WebPAnimatedFish GetClosestFish(Vector2 center)
    {
        WebPAnimatedFish bestFish = null;
        float minDist = float.MaxValue;

        foreach (var fish in activeFish) {
            if (fish.isStopped) continue; // 変身中の魚は対象外

            float d = Vector2.Distance((Vector2)fish.transform.position, center);
            if (d < minDist && d < attractionRadius) {
                minDist = d;
                bestFish = fish;
            }
        }
        return bestFish;
    }

    bool IsInArmArea2D(Vector3 elbow3D, Vector3 wrist3D)
    {
        Vector2 elbow = (Vector2)elbow3D;
        Vector2 wrist = (Vector2)wrist3D;
        Vector2 armLine = wrist - elbow;
        float armLen = armLine.magnitude;
        Vector2 armDir = armLine.normalized;
        Vector2 toFish = (Vector2)transform.position - elbow;
        float projection = Vector2.Dot(toFish, armDir);
        if (projection >= 0 && projection <= armLen) {
            float distToLine = Mathf.Abs(armDir.x * toFish.y - armDir.y * toFish.x);
            return distToLine <= armScanWidth;
        }
        return false;
    }

    void ApplyAttraction2D(Vector2 targetCenter)
    {
        transform.position = Vector3.MoveTowards(transform.position, (Vector3)targetCenter, attractionSpeed * Time.deltaTime);
    }

    void ExecuteTransform() {
        Debug.Log("<color=red>★★ QRコード化！ 30秒インターバル開始 ★★</color>");
        isStopped = true;
        lastTransformTime = Time.time; 
        _ = TransformToQrCode();
    }

    async Task TransformToQrCode() {
        isPlaying = false; 
        string qrApiUrl = $"https://api.qrserver.com/v1/create-qr-code/?size=256x256&data={Uri.EscapeDataString(fishUrl)}";
        using (var request = UnityWebRequestTexture.GetTexture(qrApiUrl)) {
            var op = request.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            if (request.result == UnityWebRequest.Result.Success) {
                Texture2D qrTexture = DownloadHandlerTexture.GetContent(request);
                spriteRenderer.sprite = Sprite.Create(qrTexture, new Rect(0, 0, qrTexture.width, qrTexture.height), new Vector2(0.5f, 0.5f));
                await Task.Delay(REVERT_DELAY_MS); // 10秒表示
                if (this != null) RevertToFish();
            } else { isStopped = false; }
        }
    }

    void RevertToFish() { isStopped = false; SetTarget(); _ = PlayLoop(); }
    void MoveFish() { if (targetPosition == Vector3.zero) SetTarget(); transform.position = Vector3.MoveTowards(transform.position, targetPosition, moveSpeed * Time.deltaTime); if (Vector3.Distance(transform.position, targetPosition) < 0.3f) SetTarget(); }
    void SetTarget() { targetPosition = new Vector3(UnityEngine.Random.Range(minX, maxX), UnityEngine.Random.Range(minY, maxY), 0); }
    async Task PlayLoop() { isPlaying = true; int prev = 0; while (isPlaying) { foreach (var f in frames) { if (!isPlaying) break; spriteRenderer.sprite = f.sprite; int delay = Mathf.Max(0, f.time - prev); prev = f.time; await Task.Delay(delay); } prev = 0; } }
    async Task<byte[]> DownloadWebP(string url) { using var uwr = UnityWebRequest.Get(url); var op = uwr.SendWebRequest(); while (!op.isDone) await Task.Yield(); return uwr.result == UnityWebRequest.Result.Success ? uwr.downloadHandler.data : null; }
    unsafe List<(Sprite, int)> DecodeWebP(byte[] bytes) { 
        var list = new List<(Sprite, int)>(); WebPAnimDecoderOptions opt; NativeLibwebpdemux.WebPAnimDecoderOptionsInit(&opt); opt.color_mode = WEBP_CSP_MODE.MODE_RGBA; 
        fixed (byte* p = bytes) { 
            WebPData data = new WebPData { bytes = p, size = (UIntPtr)bytes.Length }; WebPAnimDecoder* dec = NativeLibwebpdemux.WebPAnimDecoderNew(&data, &opt); if (dec == null) return list; 
            WebPAnimInfo info; NativeLibwebpdemux.WebPAnimDecoderGetInfo(dec, &info); uint size = info.canvas_width * info.canvas_height * 4; int timestamp = 0; IntPtr buf = IntPtr.Zero; byte** ptr = (byte**)&buf; 
            for (int i = 0; i < (int)info.frame_count; i++) { if (NativeLibwebpdemux.WebPAnimDecoderGetNext(dec, ptr, &timestamp) == 0) break; Texture2D tex = new Texture2D((int)info.canvas_width, (int)info.canvas_height, TextureFormat.RGBA32, false); tex.LoadRawTextureData(buf, (int)size); tex.Apply(); list.Add((Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f), timestamp)); } NativeLibwebpdemux.WebPAnimDecoderDelete(dec); 
        } return list; 
    }
}