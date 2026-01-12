using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public class InputFieldDisplay : NetworkBehaviour
{
    [Header("UI Components")]
    [Tooltip("텍스트를 가져올 입력 필드입니다.")]
    public TMP_InputField inputField;

    [Tooltip("입력 필드 내용을 보여줄 텍스트 컴포넌트입니다.")]
    public TMP_Text displayText;

    private readonly NetworkVariable<FixedString128Bytes> syncText = new NetworkVariable<FixedString128Bytes>(
        "",
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public override void OnNetworkSpawn()
    {
        syncText.OnValueChanged += OnTextValueChanged;

        // IsOwner일 때만 inputField의 이벤트 리스너를 추가합니다.
        // 이 스크립트의 인스펙터에 inputField를 직접 연결해야 합니다.
        if (IsOwner)
        {
            if (inputField != null)
            {
                inputField.onEndEdit.AddListener(OnEndEdit);
            }
        }

        if (IsServer)
        {
            syncText.Value = inputField.text;
        }

        if (displayText != null)
        {
            displayText.text = syncText.Value.ToString();
        }
    }

    public override void OnNetworkDespawn()
    {
        syncText.OnValueChanged -= OnTextValueChanged;
        if (inputField != null)
        {
            inputField.onEndEdit.RemoveListener(OnEndEdit);
        }
    }

    private void OnTextValueChanged(FixedString128Bytes oldText, FixedString128Bytes newText)
    {
        if (displayText != null)
        {
            displayText.text = newText.ToString();
        }
    }

    private void OnEndEdit(string text)
    {
        if (IsOwner)
        {
            SetTextServerRpc(text);
        }
    }

    [ServerRpc]
    private void SetTextServerRpc(string text)
    {
        syncText.Value = new FixedString128Bytes(text);
    }
}