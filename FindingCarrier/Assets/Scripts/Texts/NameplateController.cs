// NameplateController.cs
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RectTransform))]
public class NameplateController : MonoBehaviour
{
    public TextMeshProUGUI nameText; // prefab 안의 TMP UGUI
    private Transform followTarget;
    private RectTransform canvasRect;
    private Vector3 worldOffset = Vector3.up * 2f;

    private RectTransform rt;

    private void Awake()
    {
        rt = GetComponent<RectTransform>();
    }

    public void Initialize(Transform follow, RectTransform canvasRect, Vector3 offset, string initialText)
    {
        followTarget = follow;
        this.canvasRect = canvasRect;
        worldOffset = offset;
        if (nameText == null)
        {
            nameText = GetComponentInChildren<TextMeshProUGUI>(true);
        }
        SetText(initialText);
    }

    public void SetText(string text)
    {
        if (nameText == null) return;
        nameText.text = text;
    }

    private void LateUpdate()
    {
        if (followTarget == null || canvasRect == null) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 worldPos = followTarget.position + worldOffset;
        Vector3 screenPoint = cam.WorldToScreenPoint(worldPos);

        // 만약 뒤에 있다면 화면 밖으로 치우거나 숨김 처리 가능.
        bool behind = screenPoint.z < 0f;
        if (behind)
        {
            // 뒤에 있어도 보이게 하고 싶다면 주석 처리
            // gameObject.SetActive(false);
            // return;
            // 대안: 반대쪽에 놓기
        }
        else
        {
            gameObject.SetActive(true);
        }

        // Screen point -> Canvas local point 변환
        Vector2 localPoint;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screenPoint, null, out localPoint);
        rt.anchoredPosition = localPoint;
    }
}
