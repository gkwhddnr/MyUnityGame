using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 역할 및 게임 설명을 페이지 단위로 순환하며 보여주는 스크립트입니다.
/// "다음" 및 "이전" 버튼 클릭 시 순환하며 갱신됩니다.
/// </summary>
public class PageNavigator : MonoBehaviour
{
    [Header("UI References")]
    public Button nextButton;           // "다음" 버튼
    public Button prevButton;           // "이전" 버튼
    public TextMeshProUGUI titleText;   // 직업 이름 및 게임 규칙 출력 텍스트
    public TextMeshProUGUI descriptionText; // 직업 설명 및 게임 설명 출력 텍스트
    public ScrollRect scrollRect;

    [Header("Content Arrays")]
    [Tooltip("페이지별로 표시할 제목(직업 이름 및 게임 규칙)")]
    public string[] titles = new string[] { "<color=#DC143C>게임목표</color> ", "<color=#7FFFD4>게임하는 방법</color> ", "방독면(능력 : 보호)", "보균자(능력 : 감염)", "빌런(능력 : 진단)", "과학자(능력 : 백신)", "의사(능력 : 진단)", "간호사(능력 : 간호)", "약사(능력 : 복용)", "군인(능력 : 감금, 사살)", "기술자(능력 : 순찰)", "비상벨(역할 : 호출)", "좀비(역할 : 감염 및 증식)", "숨는 곳", "조작키", "설명 끝" };
    [Tooltip("페이지별로 표시할 설명(직업 및 게임 설명)")]
    public string[] descriptions = new string[] {
        "<color=#00FFFF>과학자</color> 는 <color=red>보균자</color>를 찾아 <color=#00FFFF>백신</color>을 꽂거나 8일을 버텨 <color=green>생존자캠프</color>가 도착할 때까지 <color=red>보균자</color>한테서 버티면 승리하고, <color=red>보균자</color>는 과학자를 찾아 <color=red>감염</color>시켜 좀비로 만들면 승리한다.\n",

        "- 8일 동안 생존한다. \n- 8개의 직업들을 모든 플레이어들에게 랜덤으로 주워진다. \n- <color=orange>낮</color> / <color=black>밤</color>으로 나뉘고 낮이 되면 모든 플레이어는 움직일 수 있고 밤이 되면 <color=red>보균자</color>만 움직일 수 있다. \n※ 밤시간은 <color=yellow>30초</color>이며 밤시간동안 남은 시간을 볼 수 없다. \n- 모든 플레이어들에게는 기본적으로 방독면 1개씩 주워진다. \n",

        "밤이 되기 전 낮 시간에만 사용이 가능하며, 사용 시 밤에 '한 번' 보균자의 감염으로부터 보호한다.\n",

        "- '<color=red>감염</color> '은 1일에 1번씩 밤에 생존자  1명만 감염 시킬 수 있다.\n다음날 아침에 감염에 성공하면 그 생존자는 사망하고 좀비를 소환한다. (보균자한테만 때리지 않는다.)\n- 당신의 조력자, '<color=orange>빌런</color>'이 숨어있으며 그가 누군지 유추하여 과학자가 누군지 함께 유추해야 한다.\n- 밤이 되면 이동속도가 증가하고, 낮이 되면 원래대로 돌아간다.\n- 밤시간 동안, 숨을 곳을 탐색하면 밤시간에서 3초씩 감소된다.\n<color=red>★ 7일째 밤까지 살아남을 시 자신도 좀비로 변하여 생존자캠프가 도착할 때까지 과학자를 찾아 죽여야 한다.  \n",

        "- '<color=orange>진단</color> '은 아침에 1번씩, 플레이어를 검사하여 그 사람이 보균자인지 아닌지 알 수 있다.\n- <color=orange>빌런</color> 과 똑같은 능력을 가진 '<color=blue>의사</color>'가 있는데, 자신이 '의사'인 척 연기하는 것이 중요하다.\n- 보균자가 아닌 다른 플레이어를 선동하여 과학자가 백신을 잘못 쓰게 하도록 유도해야 한다.\n- 보균자를 유추하여 그와 함께 승리로 이끌어야 한다.\n",

        "★'<color=#00FFFF>백신</color> '은 <color=red>'한 번'</color> 만 사용이 가능하며, 보균자를 절멸시킬 때 사용한다. 단, 백신을 보균자가 아닌 사람에게 꽂으면 패배한다.\n- 보균자로부터 살아남아야 하며, 과학자가 죽을 시 게임이 끝난다.\n- '의사'가 누군지 유추하여 그와 함께 '보균자'가 누군지 유추해야 한다.\n",

        "- '<color=blue>진단</color> '은 아침에 1번씩, 플레이어를 검사하여 그 사람이 보균자인지 아닌지 알 수 있다.\n- 의사와 똑같은 능력을 가진 '<color=orange>빌런</color>'이 있는데, 자신이 의사임을 과학자에게 어필하는 것이 중요하다.\n- '보균자'를 찾아내어 '과학자'에게 알려 그와 함께 승리로 이끌어야 한다.\n",

        "- '<color=#FBCEB1>간호</color> '는 아침에 1번씩, 다른 플레이어를 하루밤동안 보균자의 감염으로부터 보호할 수 있다.\n- '군인' 또는 '과학자'를 찾아내어 보균자의 감염으로부터 지켜줘야 한다.\n",

        "- '<color=#7FFFD4>복용</color> '은 '한 번' 만 사용이 가능하며, 사용 시 밤에 보균자가 자신을 감염시킨다면, 다음 날 아침에 <color=#7FFFD4>복용약</color>에 의해 소화되어 바이러스가 절멸된다. → 과학자팀 승리 \n- '복용' 사용 후, 다음날에 감염되지 않는다면 효과가 사라진다.\n- 자신이 중요직업임을 '보균자'한테 알려 자신에게 감염시키도록 유도해야 한다.\n",

        "- '<color=#000080>감금</color> '은 아침에 1번씩, 드론을 불러 플레이어 1명을 로비에 감금시킬 수 있다. 밤이 되기 20초 전에 풀려난다.\n- '<color=#000080>사살</color>'은 플레이어가 죽어서 좀비가 되었을 때 범위내에 있을 시 총알을 발사하여 좀비를 죽일 수 있다.  \n",

        "- '<color=#808000>순찰</color> '은 아침에 정해진 일정 '배터리' 개수 이상을 찾아 사용할 수 있다. \n-'배터리'는 <color=yellow>상호작용(e키)</color> 를 통해서 얻을 수 있다.\n- 능력 사용 시, 정해진 장소에 드론 을 설치하고 밤이 되면 모든 플레이어들에게 지정된 장소의 시야를 보여줄 수 있다. 단, 능력을 재사용하려면 '배터리'를 다시 모아야 한다.\n",

        "아침 에 1번, 로비에 살아있는 절반 이상 의 플레이어가 모일 시 자동 호출하며, 살아있는 모든 플레이어들을 로비로 호출시킨다. \n",

        "보균자에 의해 태어난 <color=red>감염된 시체</color> , 보균자 를 제외한 모든 생존자 들을 발견하면 죽일 때까지 쫓아간다. 단, 생존자가 몸을 숨는다면 먹잇감을 찾기 위해 주변을 배회한다.\n",

        "숨을 수 있는 오브젝트 와 가림막으로 전시된 오브젝트 가 있습니다. 밤이 되기 전에  오브젝트 안으로 들어가 몸을 숨으십쇼.",

        "<color=#00FF7F>조작키</color> :  <color=green>WASD</color> \n<color=yellow>Y키 :</color>  <color=#00FFFF>자유시점모드</color>  ↔ <color=#00FA9A>3인칭 관찰자(캐릭터) 시점</color> \n<color=#FF8C00>E키 : 상호작용\n   EX) 숨는 곳에 숨기/나오기, 문 열기/닫기</color>",

        "모든 룰을 숙지하셨다면 <color=#00FFFF>스페이스 를 눌러 설명창을 숨길 수 있습니다."
    };

    private int currentIndex = 0;

    private void Awake()
    {
        // 버튼의 클릭 이벤트에 메서드 연결
        if (nextButton != null)
            nextButton.onClick.AddListener(NextPage);
        if (prevButton != null)
            prevButton.onClick.AddListener(PrevPage);
    }

    private void Start()
    {
        UpdateUI();
    }

    /// <summary>
    /// 다음 페이지로 이동
    /// </summary>
    public void NextPage()
    {
        if (titles.Length == 0 || descriptions.Length == 0)
            return;

        currentIndex = (currentIndex + 1) % titles.Length;
        UpdateUI();
    }

    /// <summary>
    /// 이전 페이지로 이동
    /// </summary>
    public void PrevPage()
    {
        if (titles.Length == 0 || descriptions.Length == 0)
            return;

        currentIndex = (currentIndex - 1 + titles.Length) % titles.Length;
        UpdateUI();
    }

    /// <summary>
    /// UI 텍스트를 현재 인덱스의 콘텐츠로 갱신
    /// </summary>
    private void UpdateUI()
    {
        if (titleText != null && titles.Length > 0)
            titleText.text = titles[currentIndex];

        if (descriptionText != null && descriptions.Length > 0)
            descriptionText.text = descriptions[currentIndex];

        // 레이아웃 강제 재빌드 및 스크롤 위치 초기화
        if (scrollRect != null && scrollRect.content != null)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(scrollRect.content);
            scrollRect.verticalNormalizedPosition = 1f;
        }
    }
}
