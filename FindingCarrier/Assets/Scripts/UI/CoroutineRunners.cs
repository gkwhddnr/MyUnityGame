using System.Collections;
using UnityEngine;
/// <summary>

public class CoroutineRunner : MonoBehaviour
{
    private static CoroutineRunner _instance;
    public static CoroutineRunner Instance
    {
        get
        {
            if (_instance == null)
            {
                // 기존 인스턴스 찾기
                _instance = FindFirstObjectByType<CoroutineRunner>();
            }
            if (_instance == null)
            {
                // 없으면 새 GameObject로 생성
                var go = new GameObject("CoroutineRunner");
                _instance = go.AddComponent<CoroutineRunner>();
                DontDestroyOnLoad(go);
            }
            return _instance;
        }
    }

    // 편의 래퍼 (선택)
    public Coroutine Run(IEnumerator routine)
    {
        return StartCoroutine(routine);
    }
}
