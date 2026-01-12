using System.Collections;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;


public class PasswordVisibilityNetwork : NetworkBehaviour
{
    // 편의용 싱글톤(씬에 한 개만 두세요)
    public static PasswordVisibilityNetwork Instance { get; private set; }

    private readonly NetworkVariable<FixedString128Bytes> syncPasswordText = new NetworkVariable<FixedString128Bytes>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<bool> syncPasswordVisibility = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public event System.Action<string> OnPasswordReceived;

    private bool initialSyncCompleted = false;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(this);
    }
    private void Start()
    {
        if (IsClient && !IsServer)
        {
            StartCoroutine(RequestPasswordAfterDelay());
        }
    }

    private IEnumerator RequestPasswordAfterDelay()
    {
        yield return new WaitForSeconds(1f); // 네트워크 안정화 시간

        if (IsClient && PasswordVisibilityNetwork.Instance != null)
        {
            Debug.Log("[Client] 서버에 비밀번호 요청 전송");
            PasswordVisibilityNetwork.Instance.RequestPasswordFromServerRpc();
        }
    }



    public override void OnNetworkSpawn()
    {
        // NetworkVariable 리스너 등록
        syncPasswordText.OnValueChanged += OnPasswordTextChanged;
        syncPasswordVisibility.OnValueChanged += OnPasswordVisibilityChanged;

        if (IsServer)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        }

        if (TogglePasswordUI.Instance != null)
        {
            StartCoroutine(DelayedStateRestore());
        }
    }

    private System.Collections.IEnumerator DelayedStateRestore()
    {
        yield return new WaitForSeconds(4f); // 네트워크 안정화 대기

        var ui = TogglePasswordUI.Instance;
        if (ui != null && ui.IsCurrentlyHost())
        {
            ui.RestoreStateAfterNetworkReconnect();
        }
    }

    public override void OnNetworkDespawn()
    {
        // NetworkVariable 리스너 해제
        syncPasswordText.OnValueChanged -= OnPasswordTextChanged;
        syncPasswordVisibility.OnValueChanged -= OnPasswordVisibilityChanged;

        if (IsServer && NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
    }

    private void OnClientConnected(ulong clientId)
    {
        // 호출은 서버에서만
        if (!IsServer) return;

        var clientParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } }
        };

        // 대상 클라이언트에게만 현재 상태를 보내 주는 ClientRpc
        SendCurrentStateClientRpc(syncPasswordText.Value.ToString(),
                                  syncPasswordVisibility.Value,
                                  clientParams);
    }


    [ClientRpc]
    private void SendCurrentStateClientRpc(string pwd, bool visible, ClientRpcParams _)
    {
        var ui = TogglePasswordUI.Instance;
        if (ui == null) return;

        // 토글 상태는 호스트만 보이게, 클라이언트는 false
        ui.ApplyVisibilityLocal(NetworkManager.Singleton.IsHost ? visible : false);

        // **여기서 반드시** 받은 비밀번호를 session list UI 에도 채워 줍니다
        ui.passwordDisplay.text = pwd;
        // UIScreenTransitionManager.Instance.passwordDisplay.text = pwd;
    }

    // 비밀번호 텍스트 변경 시 UI 업데이트
    private void OnPasswordTextChanged(FixedString128Bytes oldText, FixedString128Bytes newText)
    {
        var ui = TogglePasswordUI.Instance;
        if (ui != null && ui.passwordDisplay != null)
        {
            bool shouldShowPassword = IsServer && IsHost() && syncPasswordVisibility.Value;
            ui.passwordDisplay.text = shouldShowPassword ? newText.ToString() : "";
        }
        if (!IsServer && !string.IsNullOrEmpty(newText.ToString()))
        {
            OnPasswordReceived?.Invoke(newText.ToString());
        }

    }

    // 기존 로직을 NetworkVariable 변경에 따라 수정
    private void OnPasswordVisibilityChanged(bool oldVisible, bool newVisible)
    {
        var ui = TogglePasswordUI.Instance;
        if (ui != null)
        {
            // ⭐ 방장인 경우만 실제 가시성 상태를 적용 ⭐
            if (IsServer && IsHost())
            {
                ui.ApplyVisibilityLocal(newVisible);
                if (ui.passwordDisplay != null)
                {
                    ui.passwordDisplay.text = newVisible ? syncPasswordText.Value.ToString() : "";
                }
            }
            else
            {
                // 후입장 클라이언트는 항상 false
                ui.ApplyVisibilityLocal(false);
                if (ui.passwordDisplay != null)
                {
                    ui.passwordDisplay.text = "";
                }
            }
        }
    }

    public void RequestSetPasswordVisibility(bool visible)
    {
        ApplyLocally(visible);

        if (IsServer)
        {
            // 서버라면 즉시 브로드캐스트
            syncPasswordVisibility.Value = visible;
            BroadcastPasswordVisibilityClientRpc(visible);
        }
        else
        {
            // 클라이언트이면 서버에게 요청
            SetPasswordVisibilityServerRpc(visible);
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestPasswordFromServerRpc(ServerRpcParams rpcParams = default)
    {
        var clientId = rpcParams.Receive.SenderClientId;

        var clientParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } }
        };

        // 서버에서 직접 현재 값 전송
        SendPasswordDirectlyClientRpc(syncPasswordText.Value.ToString(), syncPasswordVisibility.Value, clientParams);
    }

    [ClientRpc]
    public void SendPasswordDirectlyClientRpc(string password, bool visible, ClientRpcParams _)
    {
        Debug.Log($"[Password Sync] 직접 받은 비밀번호 = '{password}', visible={visible}");

        var ui = TogglePasswordUI.Instance;
        if (ui != null)
        {
            ui.ApplyVisibilityLocal(false); // 후입장 클라이언트는 항상 false
            if (ui.passwordDisplay != null)
            {
                ui.passwordDisplay.text = ""; // UI에는 안 보임
            }
        }

        // 내부 저장용: password는 전달받았다고 알려주기 위해
        OnPasswordReceived?.Invoke(password);
    }


    private void ApplyLocally(bool visible)
    {
        // 바로 로컬 UI에도 적용하게 하기 ? TogglePasswordUI.Instance가 있으면 호출
        TogglePasswordUI.Instance?.ApplyVisibilityLocal(visible);
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestInitialPasswordSyncServerRpc(string password, bool visible)
    {
        // 서버에서 NetworkVariable 값을 변경하면 모든 클라이언트에 동기화됨
        syncPasswordText.Value = new FixedString128Bytes(password);
        syncPasswordVisibility.Value = visible;
        initialSyncCompleted = true;
    }

    [ServerRpc(RequireOwnership = false)]
    private void SetPasswordVisibilityServerRpc(bool visible, ServerRpcParams rpcParams = default)
    {
        // (선택) 여기에 권한/로그를 추가할 수 있음
        Debug.Log($"[PasswordVisibilityNetwork] Server received visibility request={visible} from client {rpcParams.Receive.SenderClientId}");
        syncPasswordVisibility.Value = visible;
        BroadcastPasswordVisibilityClientRpc(visible);
    }

    [ClientRpc]
    private void BroadcastPasswordVisibilityClientRpc(bool visible, ClientRpcParams rpcParams = default)
    {
        var ui = TogglePasswordUI.Instance;
        if (ui != null)
        {
            // ⭐ 방장만 실제 가시성 적용, 클라이언트는 항상 false ⭐
            bool actualVisibility = IsHost() ? visible : false;
            ui.ApplyVisibilityLocal(actualVisibility);
        }
    }


    public string GetSyncedPasswordText()
    {
        return syncPasswordText.Value.ToString();
    }

    public bool GetSyncedVisibility()
    {
        return syncPasswordVisibility.Value;
    }

    // 현재 클라이언트가 방장인지 확인
    private bool IsHost()
    {
        return NetworkManager.Singleton != null &&
               NetworkManager.Singleton.IsHost;
    }

    public bool IsInitialSyncCompleted()
    {
        return initialSyncCompleted;
    }
}
