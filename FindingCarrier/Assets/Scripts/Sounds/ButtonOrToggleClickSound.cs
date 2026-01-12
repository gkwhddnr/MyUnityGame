using UnityEngine;
using UnityEngine.UI;

public class ButtonOrToggleClickSound : MonoBehaviour
{
    public Button myButton; // 버튼
    public AudioSource buttonAudioSource; // 버튼 클릭 사운드

    // 추가된 부분
    public Toggle myToggle; // 토글 버튼
    public AudioSource toggleAudioSource; // 토글 클릭 사운드

    void Start()
    {
        // 버튼 클릭 이벤트에 사운드 재생 함수 연결
        if (myButton != null)
        {
            myButton.onClick.AddListener(PlayButtonSound);
        }

        // 토글 값 변경 이벤트에 사운드 재생 함수 연결
        if (myToggle != null)
        {
            myToggle.onValueChanged.AddListener(delegate {
                PlayToggleSound();
            });
        }
    }

    void PlayButtonSound()
    {
        // 버튼 사운드가 할당되어 있을 경우에만 재생
        if (buttonAudioSource != null)
        {
            buttonAudioSource.Play();
        }
    }

    // 추가된 함수
    void PlayToggleSound()
    {
        // 토글 사운드가 할당되어 있을 경우에만 재생
        if (toggleAudioSource != null)
        {
            toggleAudioSource.Play();
        }
    }
}