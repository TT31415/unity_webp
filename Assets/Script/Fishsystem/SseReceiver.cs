using System;
using System.IO;
using System.Net.Http;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;

public class SseReceiver : MonoBehaviour
{
    public static SseReceiver Instance;
    public string sseUrl = "http://localhost:3000/events";
    public GameObject fishPrefab;

    // ★腕の座標を保持
// class 内に以下の変数が揃っているか確認
public Vector3 rightWrist = new Vector3(999, 999, 0);
public Vector3 rightElbow = new Vector3(999, 999, 0);
public Vector3 leftWrist = new Vector3(999, 999, 0);
public Vector3 leftElbow = new Vector3(999, 999, 0);
    private HttpClient _client;
    private StreamReader _reader;
    private ConcurrentQueue<string> urlQueue = new ConcurrentQueue<string>();
    private bool isRunning = false;

    void Awake() { Instance = this; }
    void Start() { isRunning = true; Task.Run(() => ConnectToServer()); }

    async Task ConnectToServer()
    {
        _client = new HttpClient();
        _client.Timeout = TimeSpan.FromMilliseconds(System.Threading.Timeout.Infinite);
        try
        {
            var stream = await _client.GetStreamAsync(sseUrl);
            _reader = new StreamReader(stream);
            while (isRunning && !_reader.EndOfStream)
            {
                var line = await _reader.ReadLineAsync();
                if (!string.IsNullOrEmpty(line) && line.StartsWith("data: ")) urlQueue.Enqueue(line.Substring(6));
            }
        }
        catch (Exception e) { if (isRunning) Debug.LogError($"SSE Error: {e.Message}"); }
    }

    void Update()
    {
        if (urlQueue.TryDequeue(out string json))
        {
            var data = JsonUtility.FromJson<FishData>(json);
            if (data != null && !string.IsNullOrEmpty(data.url)) CreateFish(data.url);
        }
    }

    void CreateFish(string url)
    {
        Vector3 pos = new Vector3(UnityEngine.Random.Range(-5f, 5f), UnityEngine.Random.Range(-3f, 3f), 0);
        GameObject fish = Instantiate(fishPrefab, pos, Quaternion.identity);
        var controller = fish.GetComponent<WebPAnimatedFish>();
        if (controller != null) controller.Initialize(url);
    }

    private void OnDestroy() { isRunning = false; _reader?.Dispose(); _client?.Dispose(); }
}
[Serializable] public class FishData { public string url; }