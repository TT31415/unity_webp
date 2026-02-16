using UnityEngine;
using Mediapipe.Tasks.Vision.PoseLandmarker;

public class PoseActionController : MonoBehaviour
{
    public float multiplierX = 20f;
    public float multiplierY = 12f;

    // MediaPipeのバックグラウンドスレッドから呼ばれる
    public void ProcessPose(PoseLandmarkerResult result)
    {
        // ここで Time.frameCount を使うとエラーになるので削除したで！
        
        if (result.poseLandmarks == null || result.poseLandmarks.Count == 0)
            return;

        var landmarks = result.poseLandmarks[0].landmarks;

        if (SseReceiver.Instance != null)
        {
            // SseReceiverの「999」を実際の座標で上書きする
            SseReceiver.Instance.rightWrist = Convert(landmarks[16]);
            SseReceiver.Instance.rightElbow = Convert(landmarks[14]);
            SseReceiver.Instance.leftWrist = Convert(landmarks[15]);
            SseReceiver.Instance.leftElbow = Convert(landmarks[13]);
        }
    }

    private Vector3 Convert(Mediapipe.Tasks.Components.Containers.NormalizedLandmark l)
    {
        // 2D判定のためにZは常に0にする
        float x = (l.x - 0.5f) * multiplierX;
        float y = (0.5f - l.y) * multiplierY;
        return new Vector3(x, y, 0);
    }
}