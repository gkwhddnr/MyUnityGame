using UnityEngine;
using UnityEngine.UI; // UI를 사용하기 위해 이 줄이 필요합니다.

public class UIManager : MonoBehaviour
{
    // 유니티 인스펙터에서 텍스트 UI를 연결할 변수입니다.
    public Text messageText;

    // 다른 스크립트에서 호출하여 메시지를 업데이트할 함수입니다.
    public void DisplayMessage(string message)
    {
        if (messageText != null)
        {
            messageText.text = message;
        }
    }
}