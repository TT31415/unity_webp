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

    // ★グローバルQR管理（場に同時に1つだけ）
    private static bool isAnyQrActive = false;
    private static float lastGlobalTransformTime = -30f;

    [Header("動き・吸引設定")]
    public float moveSpeed = 2f;
    public float attractionSpeed = 8f;   
    public float attractionRadius = 15f; 
    public float armScanWidth = 2.5f;    
    
    [Header("両手をくっつけた判定")]
    public float handsTogetherThreshold = 3.0f;

    [Header("移動範囲")]
    public float minX = -10f, maxX = 10f, minY = -5f, maxY = 5f;

    private bool isStopped = false;
    private string fishUrl;
    private const float TRANSFORM_COOLDOWN = 30f;  // 30秒インターバル
    private const int REVERT_DELAY_MS = 20000;      // QR表示20秒

    private Vector3 targetPosition;
    private List<(Sprite sprite, int time)> frames = new();
    private bool isPlaying = false;

    void OnEnable() {
        activeFish.Add(this);
        Debug.Log($"[魚] OnEnable: 魚がリストに追加されました（合計: {activeFish.Count}匹）");
    }
    void OnDisable() {
        activeFish.Remove(this);
        Debug.Log($"[魚] OnDisable: 魚がリストから除去されました（残り: {activeFish.Count}匹）");
    }

    public async void Initialize(string url)
    {
        Debug.Log($"[魚] Initialize開始: URL={url}");
        this.fishUrl = url;
        SetTarget();

        Debug.Log("[魚] WebPダウンロード開始...");
        byte[] bytes = await DownloadWebP(url);
        if (bytes == null) {
            Debug.LogWarning("[魚] WebPダウンロード失敗: bytesがnull");
            return;
        }
        Debug.Log($"[魚] WebPダウンロード完了: {bytes.Length} バイト");

        // バックグラウンドスレッドでWebPデコード（生ピクセルデータ取得）
        Debug.Log("[魚] バックグラウンドでWebPデコード開始...");
        var rawFrames = await Task.Run(() => DecodeWebPRaw(bytes));
        Debug.Log($"[魚] WebPデコード完了: {rawFrames.Count} フレーム");

        // メインスレッドでTexture2D/Sprite生成
        Debug.Log("[魚] メインスレッドでSprite生成開始...");
        frames = CreateSpritesFromRaw(rawFrames);
        Debug.Log($"[魚] Sprite生成完了: {frames.Count} フレーム → アニメーション開始");
        if (frames.Count > 0) _ = PlayLoop();
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
                
                // 2. 「一番近い一匹」だけが動く権利を得る
                WebPAnimatedFish closest = GetClosestFish(handsCenter);

                if (closest == this) {
                    Debug.Log($"[魚] 両手くっつき検出！ 手の距離={handDist:F2}, 最寄り魚=自分, QR状態={isAnyQrActive}, 経過={Time.time - lastGlobalTransformTime:F1}秒");

                    // QR変身判定（場にQRなし＆30秒インターバル経過）
                    if (!isAnyQrActive && Time.time - lastGlobalTransformTime > TRANSFORM_COOLDOWN) {
                        bool rightArm = IsInArmArea2D(sse.rightElbow, sse.rightWrist);
                        bool leftArm = IsInArmArea2D(sse.leftElbow, sse.leftWrist);
                        Debug.Log($"[魚] QR条件OK → 腕判定: 右腕={rightArm}, 左腕={leftArm}");
                        if (rightArm || leftArm) {
                            ExecuteTransform();
                            return;
                        }
                    } else {
                        if (isAnyQrActive) {
                            Debug.Log("[魚] QR変身スキップ: 既にQRが場にある");
                        } else {
                            Debug.Log($"[魚] QR変身スキップ: インターバル中（残り{TRANSFORM_COOLDOWN - (Time.time - lastGlobalTransformTime):F1}秒）");
                        }
                    }
                    // 吸引実行（一番近い魚だけが吸い寄せられる）
                    ApplyAttraction2D(handsCenter);
                }
            }
        }
        MoveFish();
    }

    // 全ての魚の中から、変身中ではなく最も手に近い一匹を返す
    WebPAnimatedFish GetClosestFish(Vector2 center)
    {
        WebPAnimatedFish bestFish = null;
        float minDist = float.MaxValue;

        foreach (var fish in activeFish) {
            if (fish.isStopped) continue;
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
        Debug.Log("<color=red>★★ QRコード変身実行！ ★★</color>");
        Debug.Log($"[魚] QR化開始: URL={fishUrl}, 20秒表示 → 30秒インターバル開始");
        isStopped = true;
        isAnyQrActive = true;
        lastGlobalTransformTime = Time.time;
        _ = TransformToQrCode();
    }

    async Task TransformToQrCode() {
        isPlaying = false; 
        string qrApiUrl = $"https://api.qrserver.com/v1/create-qr-code/?size=256x256&data={Uri.EscapeDataString(fishUrl)}";
        Debug.Log($"[魚] QRコード画像取得中: {qrApiUrl}");
        using (var request = UnityWebRequestTexture.GetTexture(qrApiUrl)) {
            var op = request.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            if (request.result == UnityWebRequest.Result.Success) {
                Texture2D qrTexture = DownloadHandlerTexture.GetContent(request);
                spriteRenderer.sprite = Sprite.Create(qrTexture, new Rect(0, 0, qrTexture.width, qrTexture.height), new Vector2(0.5f, 0.5f));
                Debug.Log($"[魚] QRコード表示中（20秒間）...");
                await Task.Delay(REVERT_DELAY_MS); // 20秒表示
                if (this != null) {
                    Debug.Log("[魚] QR表示終了 → 魚に戻ります");
                    RevertToFish();
                }
            } else {
                Debug.LogWarning($"[魚] QRコード取得失敗: {request.error}");
                isAnyQrActive = false;
                isStopped = false;
            }
        }
    }

    void RevertToFish() {
        Debug.Log("[魚] 魚に復帰完了。次のQR化可能まで30秒");
        isAnyQrActive = false;
        isStopped = false;
        SetTarget();
        _ = PlayLoop();
    }

    void MoveFish() {
        if (targetPosition == Vector3.zero) SetTarget();
        transform.position = Vector3.MoveTowards(transform.position, targetPosition, moveSpeed * Time.deltaTime);
        if (Vector3.Distance(transform.position, targetPosition) < 0.3f) SetTarget();
    }

    void SetTarget() {
        targetPosition = new Vector3(UnityEngine.Random.Range(minX, maxX), UnityEngine.Random.Range(minY, maxY), 0);
    }

    async Task PlayLoop() {
        isPlaying = true; int prev = 0;
        while (isPlaying) {
            foreach (var f in frames) {
                if (!isPlaying) break;
                spriteRenderer.sprite = f.sprite;
                int delay = Mathf.Max(0, f.time - prev);
                prev = f.time;
                await Task.Delay(delay);
            }
            prev = 0;
        }
    }

    async Task<byte[]> DownloadWebP(string url) {
        using var uwr = UnityWebRequest.Get(url);
        var op = uwr.SendWebRequest();
        while (!op.isDone) await Task.Yield();
        return uwr.result == UnityWebRequest.Result.Success ? uwr.downloadHandler.data : null;
    }

    // バックグラウンドスレッドで実行：WebPデコードして生ピクセルデータを返す
    unsafe List<(byte[] pixels, int width, int height, int time)> DecodeWebPRaw(byte[] bytes) {
        var list = new List<(byte[], int, int, int)>();
        WebPAnimDecoderOptions opt;
        NativeLibwebpdemux.WebPAnimDecoderOptionsInit(&opt);
        opt.color_mode = WEBP_CSP_MODE.MODE_RGBA;
        fixed (byte* p = bytes) {
            WebPData data = new WebPData { bytes = p, size = (UIntPtr)bytes.Length };
            WebPAnimDecoder* dec = NativeLibwebpdemux.WebPAnimDecoderNew(&data, &opt);
            if (dec == null) return list;
            WebPAnimInfo info;
            NativeLibwebpdemux.WebPAnimDecoderGetInfo(dec, &info);
            uint size = info.canvas_width * info.canvas_height * 4;
            int timestamp = 0; IntPtr buf = IntPtr.Zero; byte** ptr = (byte**)&buf;
            for (int i = 0; i < (int)info.frame_count; i++) {
                if (NativeLibwebpdemux.WebPAnimDecoderGetNext(dec, ptr, &timestamp) == 0) break;
                byte[] pixels = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(buf, pixels, 0, (int)size);
                list.Add((pixels, (int)info.canvas_width, (int)info.canvas_height, timestamp));
            }
            NativeLibwebpdemux.WebPAnimDecoderDelete(dec);
        }
        return list;
    }

    // メインスレッドで実行：生ピクセルデータからTexture2D/Spriteを生成
    List<(Sprite sprite, int time)> CreateSpritesFromRaw(List<(byte[] pixels, int width, int height, int time)> rawFrames) {
        var result = new List<(Sprite, int)>();
        foreach (var f in rawFrames) {
            Texture2D t = new Texture2D(f.width, f.height, TextureFormat.RGBA32, false);
            t.LoadRawTextureData(f.pixels);
            t.Apply();
            result.Add((Sprite.Create(t, new Rect(0, 0, f.width, f.height), new Vector2(0.5f, 0.5f), 100f), f.time));
        }
        return result;
    }
}