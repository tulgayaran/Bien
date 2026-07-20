using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using Bien.Core;
using Bien.Core.AI;

namespace Bien.Unity
{
    /// <summary>
    /// Bien oyun masası — tüm UI kodla kurulur, sahnede prefab gerekmez.
    /// Kurulum: boş sahnede boş GameObject'e bu script'i ekle, Play'e bas.
    /// Koltuklar: 0=Sen (alt), 1=Batı (sol), 2=Kuzey (üst), 3=Doğu (sağ) — saat yönü.
    /// </summary>
    public class GameSceneController : MonoBehaviour
    {
        // Zorluklar oyun içi ZORLUK panelinden seçilir, PlayerPrefs'te kalıcıdır.
        static readonly string[] DiffKeys = { "", "diff_bati", "diff_kuzey", "diff_dogu" };

        static AiDifficulty LoadDiff(int seat) =>
            (AiDifficulty)PlayerPrefs.GetInt(DiffKeys[seat], (int)AiDifficulty.Normal);

        static void SaveDiff(int seat, AiDifficulty d) =>
            PlayerPrefs.SetInt(DiffKeys[seat], (int)d);

        [Header("Debug")]
        [Tooltip("AI elleri açık oynanır ve kararların gerekçesi ekrana yazılır")]
        public bool debugMode = true;

        const float CARD_W = 200f, CARD_H = 280f;          // el kartları
        const float TRICK_W = 165f, TRICK_H = 231f;        // merkezdeki oynanmış kartlar
        static readonly string[] SeatNames = { "SEN", "BATI", "KUZEY", "DOĞU" };

        Canvas _canvas;
        Font _font;
        RectTransform _root, _handArea, _bidPanel, _popup;
        readonly RectTransform[] _trickSlots = new RectTransform[4];
        readonly Text[] _seatLabels = new Text[4];
        readonly Text[] _bidLabels = new Text[4];
        readonly RectTransform[] _aiBackAreas = new RectTransform[3]; // seat 1,2,3
        Image _trumpImage; Text _trumpText, _roundText, _scoreText, _statusText, _bidTotalText;
        AiDifficulty[] _seatDiffs;

        // --- UNDO: deterministik yeniden oynatma. Oyun tohumu + insan hamleleri kaydedilir;
        // geri almada motor sıfırdan kurulup kayıt son hamle hariç sessizce tekrar oynatılır.
        // Aynı tohum → AI'lar birebir aynı tepkiyi verir; hafıza/plan dahil tam tutarlılık. ---
        struct HAct { public char K; public int V; public Card C; }   // K: b=ihale r=revizyon p=kart
        readonly List<HAct> _acts = new();
        Queue<HAct> _replay;
        bool _fast;
        int _seed, _actsAtRoundStart;
        Button _undoBtn;
        readonly int?[] _bidsThisRound = new int?[4];
        readonly List<RoundResult> _history = new();
        RoundConfig _curRound;
        int _curDealer;
        RectTransform _fxLayer, _safe;
        Vector2[] _slotPos;

        GameEngine _engine;
        HumanAgent _human;
        readonly List<GameObject> _handCards = new();
        readonly List<GameObject> _trickCards = new();
        Task _gate = Task.CompletedTask;
        TaskCompletionSource<bool> _continueTcs;
        int[] _lastBids = new int[4];
        int[] _tricksWonLive = new int[4];

        // ------------------------------------------------------------------ setup
        void Awake()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            BuildUI();
        }

        void Start()
        {
            _seed = Environment.TickCount;
            StartNewEngine();
        }

        async void StartNewEngine()
        {
            var rng = new System.Random(_seed);
            _history.Clear();
            HidePopup();
            ClearTrick();
            _human = new HumanAgent();
            // İnsan istekleri: kayıt kuyruğu doluysa (undo tekrarı) otomatik cevapla, boşsa UI
            _human.OnBidRequested = (rc, forb, bids) =>
            {
                if (_replay != null && _replay.Count > 0) { _human.SubmitBid(_replay.Dequeue().V); return; }
                FinishReplay(); ShowBidPanel(rc, forb, bids);
            };
            _human.OnRevisionRequested = (desired, bids, rc) =>
            {
                if (_replay != null && _replay.Count > 0)
                { var a = _replay.Dequeue(); _human.SubmitRevision(a.V < 0 ? (int?)null : a.V); return; }
                FinishReplay(); ShowRevisionPanel(desired, bids, rc);
            };
            _human.OnCardRequested = (hand, led, tr) =>
            {
                if (_replay != null && _replay.Count > 0) { _human.SubmitCard(_replay.Dequeue().C); return; }
                FinishReplay(); EnableHandSelection(hand, led, tr);
                _undoBtn.interactable = _acts.Count > _actsAtRoundStart &&
                                        _acts.Count > 0 && _acts[_acts.Count - 1].K == 'p';
            };

            Func<Task> gate = () => _gate;
            var agents = new IPlayerAgent[4];
            agents[0] = new PacedAgent(_human, gate, 0);
            AiLogger.StartSession();
            _seatDiffs = new[] { AiDifficulty.Normal, LoadDiff(1), LoadDiff(2), LoadDiff(3) };
            AiLogger.Write($"Masa: BATI={LoadDiff(1)} KUZEY={LoadDiff(2)} DOĞU={LoadDiff(3)}");
            var seatDiffs = _seatDiffs;
            DebugLine($"Masa kuruldu → BATI={seatDiffs[1]}  KUZEY={seatDiffs[2]}  DOĞU={seatDiffs[3]}");
            for (int s = 1; s <= 3; s++)
            {
                var ai = AiFactory.Create(seatDiffs[s], rng);
                if (ai is TableAgent ta)
                {
                    int seat = s;
                    ta.Debug = msg => AiLogger.Write($"{SeatNames[seat]}: {msg}");
                }
                agents[s] = new PacedAgent(ai, gate, 550);
            }
            _engine = new GameEngine(agents, rng);
            _engine.InterRoundGate = InterRound;

            var ev = _engine.Events;
            ev.RoundStarted += OnRoundStarted;
            ev.HandsDealt += OnHandsDealt;
            ev.BidMade += OnBidMade;
            ev.BidRevised += (s, o, n) =>
            {
                _bidsThisRound[s] = n;
                _bidLabels[s].text = $"İhale: {n}*";
                SetStatus($"{SeatNames[s]} ihalesini {o}→{n} değiştirdi, dağıtıcı kurtarıldı");
                UpdateBidTotal();
            };
            ev.DealerForcedToChange += s => SetStatus($"{SeatNames[s]} ihalesini bozmak zorunda");
            ev.CardPlayed += OnCardPlayed;
            ev.TrickWon += OnTrickWon;
            ev.RoundEnded += OnRoundEnded;
            ev.GameEnded += OnGameEnded;

            try { await _engine.PlayGameAsync(firstDealer: UnityEngine.Random.Range(0, 4)); }
            catch (Exception e) { Debug.LogException(e); }
        }

        // ------------------------------------------------------------------ engine events
        void RecordAct(char k, int v, Card c = default)
        {
            _acts.Add(new HAct { K = k, V = v, C = c });
            if (_undoBtn != null) _undoBtn.interactable = false;
        }

        /// <summary>Son oynanan kartını geri çeker. Ardışık basışlarla tur başına kadar gidilir.</summary>
        void Undo()
        {
            if (_fast) return;
            if (_acts.Count == 0 || _acts.Count <= _actsAtRoundStart) return;
            if (_acts[_acts.Count - 1].K != 'p') return; // yalnız kart hamlesi geri alınır
            _acts.RemoveAt(_acts.Count - 1);
            _fast = true;
            PacedAgent.FastMode = true;
            _gate = Task.CompletedTask;
            HidePopup();
            ClearTrick();
            _replay = new Queue<HAct>(_acts);
            StartNewEngine(); // eski motor terk edilir (insan beklemesinde asılı, zararsız)
        }

        void FinishReplay()
        {
            if (!_fast) return;
            _fast = false;
            PacedAgent.FastMode = false;
            SetStatus("Geri alındı — sıra sende");
        }

        void OnRoundStarted(RoundConfig rc, int dealer)
        {
            _actsAtRoundStart = _acts.Count - (_replay?.Count ?? 0);
            if (_undoBtn != null) _undoBtn.interactable = false;
            AiLogger.Write($"\n--- TUR {rc.RoundIndex + 1}/16 · {rc.CardsPerPlayer} kart · {(rc.HasTrump ? "kozlu" : "SANS")} · dağıtan {SeatNames[dealer]} ---");
            _curRound = rc;
            _curDealer = dealer;
            for (int i = 0; i < 4; i++) _bidsThisRound[i] = null;
            _bidTotalText.text = "";
            _tricksWonLive = new int[4];
            _roundText.text = $"Tur {rc.RoundIndex + 1}/16  •  {rc.CardsPerPlayer} kart" + (rc.HasTrump ? "" : "  •  SANS");
            for (int i = 0; i < 4; i++)
            {
                _bidLabels[i].text = "";
                string diff = (debugMode && i > 0 && _seatDiffs != null)
                    ? $" [{_seatDiffs[i]}]" : "";
                _seatLabels[i].text = SeatNames[i] + diff + (i == dealer ? "  (dağıtan)" : "");
            }
            ClearTrick();
            SetStatus("Kartlar dağıtılıyor...");
        }

        void OnHandsDealt(IReadOnlyList<Card>[] hands, Card? trumpCard)
        {
            _trumpImage.enabled = false; _trumpText.text = "";
            if (_fast) { RenderDealt(hands, trumpCard); return; } // tekrar oynatma: animasyonsuz
            var tcs = new TaskCompletionSource<bool>();
            _gate = tcs.Task; // dağıtım bitene kadar motor (ihale) bekler
            StartCoroutine(DealAnimation(hands, trumpCard, tcs));
        }

        System.Collections.IEnumerator DealAnimation(IReadOnlyList<Card>[] hands, Card? trumpCard,
                                                     TaskCompletionSource<bool> done)
        {
            SetStatus($"{SeatNames[_curDealer]} dağıtıyor...");
            Vector2 from = SeatFxPos(_curDealer);
            int n = _curRound.CardsPerPlayer;
            float stagger = n * 4 > 24 ? 0.032f : 0.055f;

            for (int c = 0; c < n; c++)
                for (int p = 1; p <= 4; p++)
                {
                    int seat = (_curDealer + p) % 4;
                    var rt = MakeCardImage(_fxLayer, CardSprites.Back, CARD_W * 0.55f, CARD_H * 0.55f);
                    rt.anchoredPosition = from;
                    StartCoroutine(FlyAndVanish(rt, from, SeatFxPos(seat), 0.22f));
                    yield return new WaitForSeconds(stagger);
                }
            yield return new WaitForSeconds(0.25f);

            RenderDealt(hands, trumpCard);
            done.TrySetResult(true);
        }

        void RenderDealt(IReadOnlyList<Card>[] hands, Card? trumpCard)
        {
            RenderHumanHand(hands[0], interactable: false);
            for (int s = 1; s <= 3; s++)
            {
                if (debugMode) RenderAiFaces(s, hands[s]);
                else RenderAiBacks(s, hands[s].Count);
            }
            if (trumpCard.HasValue)
            {
                _trumpImage.enabled = true;
                _trumpImage.sprite = CardSprites.Get(trumpCard.Value);
                _trumpText.text = "KOZ";
            }
            else { _trumpImage.enabled = false; _trumpText.text = "SANS"; }
        }

        Vector2 SeatFxPos(int seat) => seat switch
        {
            0 => new Vector2(0, -400),
            1 => new Vector2(-870, -40),
            2 => new Vector2(0, 420),
            _ => new Vector2(870, -40),
        };

        System.Collections.IEnumerator FlyAndVanish(RectTransform rt, Vector2 from, Vector2 to, float dur)
        {
            float t = 0;
            while (t < dur)
            {
                if (rt == null) yield break;
                t += Time.deltaTime;
                float k = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / dur), 2f); // ease-out
                rt.anchoredPosition = Vector2.LerpUnclamped(from, to, k);
                yield return null;
            }
            if (rt != null) Destroy(rt.gameObject);
        }

        void OnBidMade(int seat, int bid)
        {
            _lastBids[seat] = bid;
            _bidsThisRound[seat] = bid;
            _bidLabels[seat].text = $"İhale: {bid}";
            SetStatus($"{SeatNames[seat]}: {bid} dedi");
            UpdateBidTotal();
        }

        void UpdateBidTotal()
        {
            for (int i = 0; i < 4; i++) if (!_bidsThisRound[i].HasValue) return;
            int total = 0;
            for (int i = 0; i < 4; i++) total += _bidsThisRound[i].Value;
            int diff = _curRound.CardsPerPlayer - total; // masadaki el fazlası (+: sahipsiz el var)
            if (diff > 0)
            {
                _bidTotalText.text = $"Toplam {total}  ({diff} fazla)";
                _bidTotalText.color = new Color(0.55f, 0.85f, 1f); // bol el, rahat — soğuk renk
            }
            else if (diff < 0)
            {
                _bidTotalText.text = $"Toplam {total}  ({-diff} eksik)";
                _bidTotalText.color = new Color(1f, 0.55f, 0.35f); // el kıtlığı, kapışma — sıcak renk
            }
            else
            {
                _bidTotalText.text = $"Toplam {total}  —  BİEN!";
                _bidTotalText.color = new Color(1f, 0.85f, 0.25f);
            }
        }

        void OnCardPlayed(int seat, Card card)
        {
            if (seat == 0)
            {
                var go = _handCards.FirstOrDefault(g => (Card)g.GetComponent<CardRef>().Card == card);
                if (go != null) { _handCards.Remove(go); Destroy(go); }
                RelayoutHand();
            }
            else
            {
                var area = _aiBackAreas[seat - 1];
                if (debugMode)
                {
                    for (int i = 0; i < area.childCount; i++)
                    {
                        var cr = area.GetChild(i).GetComponent<CardRef>();
                        if (cr != null && cr.Card == card) { Destroy(cr.gameObject); break; }
                    }
                }
                else if (area.childCount > 0) Destroy(area.GetChild(area.childCount - 1).gameObject);
            }
            var tc = MakeCardImage(_trickSlots[seat], CardSprites.Get(card), TRICK_W, TRICK_H);
            _trickCards.Add(tc.gameObject);
            if (_fast) tc.anchoredPosition = Vector2.zero;
            else
            {
                Vector2 slideFrom = SeatFxPos(seat) - _slotPos[seat];
                tc.anchoredPosition = slideFrom;
                StartCoroutine(SlideIn(tc, slideFrom, Vector2.zero, 0.22f));
            }
        }

        void OnTrickWon(int winner, IReadOnlyList<Card> trick)
        {
            _tricksWonLive[winner]++;
            _bidLabels[winner].text = $"İhale: {_lastBids[winner]}  El: {_tricksWonLive[winner]}";
            SetStatus($"Eli {SeatNames[winner]} aldı");
            if (_fast) { ClearTrick(); return; }
            var tcs = new TaskCompletionSource<bool>();
            _gate = tcs.Task;
            StartCoroutine(ClearTrickAfter(0.9f, tcs));
        }

        System.Collections.IEnumerator ClearTrickAfter(float sec, TaskCompletionSource<bool> tcs)
        {
            yield return new WaitForSeconds(sec);
            ClearTrick();
            tcs.SetResult(true);
        }

        void OnRoundEnded(RoundResult r)
        {
            _history.Add(r);
            for (int p = 0; p < 4; p++)
                AiLogger.Write($"SONUÇ {SeatNames[p]}: ihale {r.Bids[p]}, aldı {r.TricksWon[p]} → {(r.Bids[p] == r.TricksWon[p] ? $"+{r.Scores[p]}" : "BATTI")}");
            UpdateScoreboard();
        }

        /// <summary>Motorun tur-arası kancası: son el 1.4sn masada kalır → tablo + Devam →
        /// kullanıcı basınca motor yeni tura (dağıtıma) geçer. Son turda ara tablo atlanır,
        /// finali GameEnded gösterir.</summary>
        Task InterRound(RoundResult r, bool wasLast)
        {
            if (_fast) return Task.CompletedTask; // tekrar oynatmada duraksız geç
            var tcs = new TaskCompletionSource<bool>();
            StartCoroutine(InterRoundFlow(wasLast, tcs));
            return tcs.Task;
        }

        System.Collections.IEnumerator InterRoundFlow(bool wasLast, TaskCompletionSource<bool> done)
        {
            yield return new WaitForSeconds(1.4f); // son el görünür kalsın
            ClearTrick();
            if (wasLast) { done.TrySetResult(true); yield break; } // finali GameEnded tablosu gösterecek
            ShowScoreTable("Devam", () => { HidePopup(); done.TrySetResult(true); });
        }

        void OnGameEnded(int[] totals)
        {
            int w = Array.IndexOf(totals, totals.Max());
            ShowScoreTable("Yeni Oyun",
                () => UnityEngine.SceneManagement.SceneManager.LoadScene(
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex),
                $"OYUN BİTTİ — {SeatNames[w]} KAZANDI!");
        }

        // ------------------------------------------------------------------ human interaction
        void ShowBidPanel(RoundConfig rc, int? forbidden, IReadOnlyList<int?> bids)
        {
            SetStatus(forbidden.HasValue ? $"İhaleni seç ({forbidden.Value} yasak — toplam el sayısına eşitleyemezsin)" : "İhaleni seç");
            BuildBidButtons(rc.CardsPerPlayer, forbidden,
                b => { RecordAct('b', b); _human.SubmitBid(b); }, null);
            _bidPanel.gameObject.SetActive(true);
        }

        void ShowRevisionPanel(int dealerDesired, IReadOnlyList<int?> bids, RoundConfig rc)
        {
            SetStatus($"Dağıtıcı {dealerDesired} demek istiyor ama yasak. İhaleni değiştirir misin?");
            BuildBidButtons(rc.CardsPerPlayer, null,
                b => { RecordAct('r', b); _human.SubmitRevision(b); },
                () => { RecordAct('r', -1); _human.SubmitRevision(null); });
            _bidPanel.gameObject.SetActive(true);
        }

        void EnableHandSelection(IReadOnlyList<Card> hand, Suit? ledSuit, Suit? trump)
        {
            SetStatus("Sıra sende — bir kart oyna");
            var legal = GameRules.LegalPlays(hand, ledSuit, trump);
            foreach (var go in _handCards)
            {
                var card = go.GetComponent<CardRef>().Card;
                bool ok = legal.Contains(card);
                var btn = go.GetComponent<Button>();
                btn.interactable = ok;
                go.GetComponent<Image>().color = ok ? Color.white : new Color(0.55f, 0.55f, 0.55f);
                var rt = (RectTransform)go.transform;
                rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, ok ? 26f : 0f);
            }
        }

        void OnHandCardClicked(GameObject go)
        {
            var card = go.GetComponent<CardRef>().Card;
            foreach (var g in _handCards)
            {
                g.GetComponent<Button>().interactable = false;
                g.GetComponent<Image>().color = Color.white;
                var rt = (RectTransform)g.transform;
                rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, 0f);
            }
            RecordAct('p', 0, card);
            _human.SubmitCard(card);
        }

        // ------------------------------------------------------------------ UI build
        void BuildUI()
        {
            var cgo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            _canvas = cgo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = cgo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(2532, 1170); // iPhone landscape
            scaler.matchWidthOrHeight = 1f; // yüksekliğe kilitle
            if (FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
#if ENABLE_INPUT_SYSTEM
                new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem),
                               typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
#else
                new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem),
                               typeof(UnityEngine.EventSystems.StandaloneInputModule));
#endif
            }

            _root = MakePanel(cgo.transform, "Root", Vector2.zero, Vector2.one, new Color(0.05f, 0.28f, 0.14f)); // çuha yeşili

            // Çentik/safe-area: zemin tam ekran, içerik güvenli alanda
            var safeGo = new GameObject("SafeArea", typeof(RectTransform), typeof(SafeAreaFitter));
            _safe = (RectTransform)safeGo.transform;
            _safe.SetParent(_root, false);

            // HUD üst şerit
            _roundText = MakeText(_safe, "Round", new Vector2(0.5f, 1f), new Vector2(0, -45), 40, TextAnchor.MiddleCenter);
            _statusText = MakeText(_safe, "Status", new Vector2(0.5f, 0f), new Vector2(0, 335), 32, TextAnchor.MiddleCenter);
            _statusText.color = new Color(1f, 0.92f, 0.6f);

            // Koz göstergesi (sol üst)
            var trumpGo = MakeCardImage(_safe, null, CARD_W * 0.72f, CARD_H * 0.72f);
            trumpGo.anchorMin = trumpGo.anchorMax = new Vector2(0f, 1f);
            trumpGo.anchoredPosition = new Vector2(160, -155);
            _trumpImage = trumpGo.GetComponent<Image>();
            _trumpText = MakeText(_safe, "TrumpLbl", new Vector2(0f, 1f), new Vector2(160, -45), 30, TextAnchor.MiddleCenter);
            ((RectTransform)_trumpText.transform).sizeDelta = new Vector2(200, 40);

            // Puan tablosu butonu (sol üst)
            var tblBtn = MakeButton(_safe, "TABLO", new Vector2(210, 64), new Vector2(0f, 1f), new Vector2(400, -45), 28);
            var diffBtn = MakeButton(_safe, "ZORLUK", new Vector2(210, 64), new Vector2(0f, 1f), new Vector2(640, -45), 28);
            diffBtn.onClick.AddListener(() =>
            {
                if (_popup.gameObject.activeSelf) return;
                ShowDifficultyPanel();
            });
            _undoBtn = MakeButton(_safe, "GERİ", new Vector2(180, 64), new Vector2(0f, 1f), new Vector2(855, -45), 28);
            _undoBtn.interactable = false;
            _undoBtn.onClick.AddListener(Undo);
            tblBtn.onClick.AddListener(() =>
            {
                if (_popup.gameObject.activeSelf) return; // tur sonu tablosu açıkken karışma
                ShowScoreTable("Kapat", HidePopup);
            });

            // Toplam ihale göstergesi (kozun altı, büyük ve kalın)
            _bidTotalText = MakeText(_safe, "BidTotal", new Vector2(0f, 1f), new Vector2(490, -155), 40, TextAnchor.MiddleCenter);
            _bidTotalText.fontStyle = FontStyle.Bold;
            ((RectTransform)_bidTotalText.transform).sizeDelta = new Vector2(420, 60);
            AddChip(_bidTotalText, new Vector2(430, 62), new Color(0f, 0f, 0f, 0.45f));

            // Skor (sağ üst)
            _scoreText = MakeText(_safe, "Score", new Vector2(1f, 1f), new Vector2(-190, -60), 28, TextAnchor.UpperLeft);
            ((RectTransform)_scoreText.transform).sizeDelta = new Vector2(300, 300);
            _scoreText.text = "";

            // Koltuk etiketleri + ihale etiketleri + AI arka alanları
            // seat1 BATI (sol), seat2 KUZEY (üst), seat3 DOĞU (sağ)
            _seatLabels[0] = MakeText(_safe, "L0", new Vector2(0f, 0f), new Vector2(190, 250), 32, TextAnchor.MiddleCenter);
            _bidLabels[0] = MakeText(_safe, "B0", new Vector2(0f, 0f), new Vector2(190, 200), 28, TextAnchor.MiddleCenter);
            _seatLabels[1] = MakeText(_safe, "L1", new Vector2(0f, 0.5f), new Vector2(340, 175), 32, TextAnchor.MiddleCenter);
            _bidLabels[1] = MakeText(_safe, "B1", new Vector2(0f, 0.5f), new Vector2(340, 125), 28, TextAnchor.MiddleCenter);
            _seatLabels[2] = MakeText(_safe, "L2", new Vector2(0.5f, 1f), new Vector2(0, -100), 32, TextAnchor.MiddleCenter);
            _bidLabels[2] = MakeText(_safe, "B2", new Vector2(0.5f, 1f), new Vector2(0, -145), 28, TextAnchor.MiddleCenter);
            _seatLabels[3] = MakeText(_safe, "L3", new Vector2(1f, 0.5f), new Vector2(-340, 175), 32, TextAnchor.MiddleCenter);
            _bidLabels[3] = MakeText(_safe, "B3", new Vector2(1f, 0.5f), new Vector2(-340, 125), 28, TextAnchor.MiddleCenter);
            foreach (var t in _bidLabels)
            {
                t.color = new Color(1f, 0.9f, 0.45f);
                t.fontStyle = FontStyle.Bold;
                AddChip(t, new Vector2(250, 44), new Color(0f, 0f, 0f, 0.40f));
            }

            _aiBackAreas[0] = MakeArea(_safe, "BacksW", new Vector2(0f, 0.5f), new Vector2(340, -40));
            _aiBackAreas[1] = MakeArea(_safe, "BacksN", new Vector2(0.5f, 1f), new Vector2(0, -255));
            _aiBackAreas[2] = MakeArea(_safe, "BacksE", new Vector2(1f, 0.5f), new Vector2(-340, -40));

            // El alanları (merkez etrafı) — sırayla alt, sol, üst, sağ
            _slotPos = new Vector2[] { new(0, -180), new(-350, 10), new(0, 175), new(350, 10) };
            for (int i = 0; i < 4; i++)
            {
                _trickSlots[i] = MakeArea(_safe, $"Trick{i}", new Vector2(0.5f, 0.5f), _slotPos[i]);
            }

            // Uçan kartlar için katman (dağıtım animasyonu burada oynar)
            _fxLayer = MakeArea(_safe, "FX", new Vector2(0.5f, 0.5f), Vector2.zero);

            // İnsan eli (alt)
            _handArea = MakeArea(_safe, "Hand", new Vector2(0.5f, 0f), new Vector2(0, 155));

            // İhale paneli
            _bidPanel = MakePanel(_safe, "BidPanel", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Color(0f, 0f, 0f, 0.86f));
            _bidPanel.sizeDelta = new Vector2(980, 620);
            _bidPanel.anchoredPosition = new Vector2(0, 80); // el kartlarının üstünde kalsın
            _bidPanel.gameObject.SetActive(false);

            // Debug paneli (sol alt)

            // Popup
            _popup = MakePanel(_safe, "Popup", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Color(0f, 0f, 0f, 0.92f));
            _popup.sizeDelta = new Vector2(980, 900);
            _popup.gameObject.SetActive(false);
        }

        void BuildBidButtons(int maxBid, int? forbidden, Action<int> onPick, Action onKeep)
        {
            foreach (Transform c in _bidPanel) Destroy(c.gameObject);
            _bidPanel.sizeDelta = new Vector2(560, 130);
            _bidPanel.anchoredPosition = new Vector2(0, -30);

            var q = MakeText(_bidPanel, "Q", new Vector2(0.5f, 1f), new Vector2(0, -30), 32, TextAnchor.MiddleCenter);
            q.text = onKeep == null ? "Kaç el alırsın?" : "Yeni ihalen? (veya değiştirme)";

            var main = MakeButton(_bidPanel, "Seç  ▾", new Vector2(300, 68), new Vector2(0.5f, 0f), new Vector2(0, 42), 34);

            // Açılır liste: 8'den çok seçenekte 2 sütuna kırılır, panelin üstünden yukarı açılır
            int extra = onKeep != null ? 1 : 0;
            int count = maxBid + 1 + extra;
            int cols = count > 8 ? 2 : 1;
            int rows = (count + cols - 1) / cols;
            const float IH = 66, IW = 292;

            var list = new GameObject("List", typeof(Image));
            var lrt = (RectTransform)list.transform;
            lrt.SetParent(_bidPanel, false);
            lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 1f);
            lrt.pivot = new Vector2(0.5f, 0f);
            lrt.anchoredPosition = new Vector2(0, 6);
            lrt.sizeDelta = new Vector2(cols * (IW + 10) + 10, rows * IH + 14);
            list.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.96f);
            list.SetActive(false);

            void AddItem(int idx, string label, bool enabled, Action act, Color? bg = null)
            {
                int r = idx / cols, col = idx % cols;
                float x = (col - (cols - 1) / 2f) * (IW + 10);
                float y = -(14 + r * IH) - (IH - 12) / 2f + 4;
                var b = MakeButton(lrt, label, new Vector2(IW, IH - 10), new Vector2(0.5f, 1f), new Vector2(x, y), 32);
                b.interactable = enabled;
                if (bg.HasValue) b.GetComponent<Image>().color = bg.Value;
                if (enabled) b.onClick.AddListener(() => act());
            }

            for (int v = 0; v <= maxBid; v++)
            {
                int bid = v;
                bool blocked = forbidden.HasValue && v == forbidden.Value;
                AddItem(v, blocked ? $"{v}  (yasak)" : v.ToString(), !blocked,
                        () => { _bidPanel.gameObject.SetActive(false); onPick(bid); },
                        blocked ? new Color(0.45f, 0.18f, 0.18f) : (Color?)null);
            }
            if (onKeep != null)
                AddItem(maxBid + 1, "DEĞİŞTİRME", true,
                        () => { _bidPanel.gameObject.SetActive(false); onKeep(); },
                        new Color(0.25f, 0.35f, 0.55f));

            main.onClick.AddListener(() => list.SetActive(!list.activeSelf));
        }

        // ------------------------------------------------------------------ rendering
        void RenderHumanHand(IReadOnlyList<Card> hand, bool interactable)
        {
            foreach (var g in _handCards) Destroy(g);
            _handCards.Clear();
            var sorted = hand.OrderBy(c => c.Suit).ThenByDescending(c => c.Rank).ToList();
            foreach (var card in sorted)
            {
                var rt = MakeCardImage(_handArea, CardSprites.Get(card), CARD_W, CARD_H);
                var go = rt.gameObject;
                go.AddComponent<CardRef>().Card = card;
                var btn = go.AddComponent<Button>();
                btn.transition = Selectable.Transition.None; // disabled yarı-saydam gri tint'i engelle
                btn.interactable = interactable;
                btn.onClick.AddListener(() => OnHandCardClicked(go));
                _handCards.Add(go);
            }
            RelayoutHand();
        }

        void RelayoutHand()
        {
            int n = _handCards.Count;
            if (n == 0) return;
            float overlap = n > 7 ? (2100f - CARD_W) / (n - 1) : CARD_W + 12;
            overlap = Mathf.Min(overlap, CARD_W + 12);
            float total = CARD_W + (n - 1) * overlap;
            for (int i = 0; i < n; i++)
            {
                var rt = (RectTransform)_handCards[i].transform;
                rt.anchoredPosition = new Vector2(-total / 2 + CARD_W / 2 + i * overlap, rt.anchoredPosition.y);
                rt.SetSiblingIndex(i);
            }
        }

        void RenderAiBacks(int seat, int count)
        {
            var area = _aiBackAreas[seat - 1];
            foreach (Transform c in area) Destroy(c.gameObject);
            float step = Mathf.Min(40f, 560f / Mathf.Max(1, count));
            for (int i = 0; i < count; i++)
            {
                var rt = MakeCardImage(area, CardSprites.Back, CARD_W * 0.58f, CARD_H * 0.58f);
                rt.anchoredPosition = new Vector2((i - (count - 1) / 2f) * step, 0);
            }
        }

        void RenderAiFaces(int seat, IReadOnlyList<Card> hand)
        {
            var area = _aiBackAreas[seat - 1];
            foreach (Transform c in area) Destroy(c.gameObject);
            var sorted = hand.OrderBy(c => c.Suit).ThenByDescending(c => c.Rank).ToList();
            float step = Mathf.Min(52f, 560f / Mathf.Max(1, sorted.Count));
            for (int i = 0; i < sorted.Count; i++)
            {
                var rt = MakeCardImage(area, CardSprites.Get(sorted[i]), CARD_W * 0.58f, CARD_H * 0.58f);
                rt.gameObject.AddComponent<CardRef>().Card = sorted[i];
                rt.anchoredPosition = new Vector2((i - (sorted.Count - 1) / 2f) * step, 0);
            }
        }

        void DebugLine(string s) => AiLogger.Write(s);

        System.Collections.IEnumerator SlideIn(RectTransform rt, Vector2 from, Vector2 to, float dur)
        {
            float t = 0;
            while (t < dur)
            {
                if (rt == null) yield break;
                t += Time.deltaTime;
                float k = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / dur), 2f);
                rt.anchoredPosition = Vector2.LerpUnclamped(from, to, k);
                yield return null;
            }
            if (rt != null) rt.anchoredPosition = to;
        }

        void ClearTrick()
        {
            foreach (var g in _trickCards) Destroy(g);
            _trickCards.Clear();
        }

        void UpdateScoreboard()
        {
            _scoreText.text = "SKOR\n" + string.Join("\n",
                _engine.TotalScores.Select((s, i) => $"{SeatNames[i],-7} {s}"));
        }

        /// <summary>4 sütun × 16 tur + toplam satırı. Batan: kırmızı X, çıkan: o tur aldığı puan.</summary>
        void ShowScoreTable(string btnLabel, Action onClick, string titleOverride = null)
        {
            foreach (Transform c in _popup) Destroy(c.gameObject);
            _popup.sizeDelta = new Vector2(1180, 1090);
            _popup.GetComponent<Image>().color = Color.black; // tam opak

            var t = MakeText(_popup, "T", new Vector2(0.5f, 1f), new Vector2(0, -52), 44, TextAnchor.MiddleCenter);
            t.text = titleOverride ?? "PUAN TABLOSU";
            t.fontStyle = FontStyle.Bold;

            float[] colX = { -440, -240, -80, 80, 240 };
            var rounds = GameStructure.BuildRounds();

            // Başlık satırı
            for (int p = 0; p < 4; p++)
            {
                var h = MakeText(_popup, $"H{p}", new Vector2(0.5f, 1f), new Vector2(colX[p + 1], -114), 32, TextAnchor.MiddleCenter);
                h.text = SeatNames[p];
                h.fontStyle = FontStyle.Bold;
                h.color = new Color(1f, 0.9f, 0.45f);
            }
            var hl = MakeText(_popup, "HL", new Vector2(0.5f, 1f), new Vector2(colX[0], -114), 28, TextAnchor.MiddleCenter);
            hl.text = "El";
            hl.color = new Color(0.7f, 0.8f, 0.75f);

            // Tur satırları
            for (int i = 0; i < rounds.Count; i++)
            {
                float y = -158 - i * 44;
                var lbl = MakeText(_popup, $"R{i}", new Vector2(0.5f, 1f), new Vector2(colX[0], y), 28, TextAnchor.MiddleCenter);
                lbl.text = rounds[i].CardsPerPlayer.ToString() + (rounds[i].HasTrump ? "" : "*");
                lbl.color = new Color(0.7f, 0.8f, 0.75f);

                if (i >= _history.Count) continue;
                var rr = _history[i];
                for (int p = 0; p < 4; p++)
                {
                    var cell = MakeText(_popup, $"C{i}_{p}", new Vector2(0.5f, 1f), new Vector2(colX[p + 1], y), 30, TextAnchor.MiddleCenter);
                    bool made = rr.Bids[p] == rr.TricksWon[p];
                    if (made) { cell.text = rr.Scores[p].ToString(); cell.color = Color.white; }
                    else { cell.text = "X"; cell.fontStyle = FontStyle.Bold; cell.color = new Color(1f, 0.35f, 0.3f); }
                }
            }

            // Toplamlar
            float ty = -158 - rounds.Count * 44 - 16;
            var tl = MakeText(_popup, "TL", new Vector2(0.5f, 1f), new Vector2(colX[0], ty), 30, TextAnchor.MiddleCenter);
            tl.text = "TOP";
            tl.fontStyle = FontStyle.Bold;
            tl.color = new Color(1f, 0.85f, 0.25f);
            for (int p = 0; p < 4; p++)
            {
                int sum = 0;
                foreach (var rr in _history) sum += rr.Scores[p];
                var tot = MakeText(_popup, $"T{p}", new Vector2(0.5f, 1f), new Vector2(colX[p + 1], ty), 34, TextAnchor.MiddleCenter);
                tot.text = sum.ToString();
                tot.fontStyle = FontStyle.Bold;
                tot.color = new Color(1f, 0.85f, 0.25f);
            }

            var btn = MakeButton(_popup, btnLabel, new Vector2(380, 92), new Vector2(0.5f, 0f), new Vector2(0, 60), 36);
            btn.onClick.AddListener(() => onClick());
            _popup.gameObject.SetActive(true);
        }

        /// <summary>Koltuk başına zorluk seçimi. Kaydet → PlayerPrefs → yeni oyunla başlar.</summary>
        void ShowDifficultyPanel()
        {
            foreach (Transform c in _popup) Destroy(c.gameObject);
            _popup.sizeDelta = new Vector2(1040, 780);
            _popup.GetComponent<Image>().color = Color.black;

            var t = MakeText(_popup, "T", new Vector2(0.5f, 1f), new Vector2(0, -55), 42, TextAnchor.MiddleCenter);
            t.text = "ZORLUK SEVİYESİ";
            t.fontStyle = FontStyle.Bold;

            var temp = new AiDifficulty[4];
            for (int s = 1; s <= 3; s++) temp[s] = LoadDiff(s);

            var diffs = new[] { AiDifficulty.Easy, AiDifficulty.Normal, AiDifficulty.Hard };
            var btnImgs = new Image[5, 3]; // satır 1-3 koltuk, 4 = TÜMÜ

            Color selCol = new Color(0.95f, 0.75f, 0.2f);
            Color offCol = new Color(0.16f, 0.45f, 0.28f);

            void Refresh()
            {
                for (int row = 1; row <= 3; row++)
                    for (int d = 0; d < 3; d++)
                        btnImgs[row, d].color = temp[row] == diffs[d] ? selCol : offCol;
            }

            string[] rowNames = { "", "BATI", "KUZEY", "DOĞU", "TÜMÜ" };
            for (int row = 1; row <= 4; row++)
            {
                float y = -140 - (row - 1) * 110;
                var lbl = MakeText(_popup, $"RL{row}", new Vector2(0.5f, 1f), new Vector2(-380, y), 32, TextAnchor.MiddleCenter);
                lbl.text = rowNames[row];
                if (row == 4) lbl.color = new Color(0.7f, 0.85f, 1f);

                for (int d = 0; d < 3; d++)
                {
                    int rr = row; int dd = d;
                    var b = MakeButton(_popup, diffs[d].ToString(), new Vector2(230, 84),
                                       new Vector2(0.5f, 1f), new Vector2(-120 + d * 250, y), 30);
                    btnImgs[row, d] = b.GetComponent<Image>();
                    b.onClick.AddListener(() =>
                    {
                        if (rr == 4) { temp[1] = temp[2] = temp[3] = diffs[dd]; }
                        else temp[rr] = diffs[dd];
                        Refresh();
                    });
                }
            }
            Refresh();

            var apply = MakeButton(_popup, "KAYDET — YENİ OYUN", new Vector2(480, 96), new Vector2(0.5f, 0f), new Vector2(-160, 60), 32);
            apply.onClick.AddListener(() =>
            {
                for (int s = 1; s <= 3; s++) SaveDiff(s, temp[s]);
                PlayerPrefs.Save();
                UnityEngine.SceneManagement.SceneManager.LoadScene(
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
            });
            var close = MakeButton(_popup, "Vazgeç", new Vector2(260, 96), new Vector2(0.5f, 0f), new Vector2(280, 60), 30);
            close.GetComponent<Image>().color = new Color(0.35f, 0.35f, 0.4f);
            close.onClick.AddListener(HidePopup);

            _popup.gameObject.SetActive(true);
        }

        void ShowPopup(string title, string body, string btnLabel, Action onClick)
        {
            foreach (Transform c in _popup) Destroy(c.gameObject);
            _popup.sizeDelta = new Vector2(980, 900);
            _popup.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.92f);
            var t = MakeText(_popup, "T", new Vector2(0.5f, 1f), new Vector2(0, -70), 44, TextAnchor.MiddleCenter);
            t.text = title;
            var b = MakeText(_popup, "B", new Vector2(0.5f, 0.5f), new Vector2(0, 40), 36, TextAnchor.MiddleCenter);
            ((RectTransform)b.transform).sizeDelta = new Vector2(900, 500);
            b.text = body;
            var btn = MakeButton(_popup, btnLabel, new Vector2(420, 110), new Vector2(0.5f, 0f), new Vector2(0, 100), 40);
            btn.onClick.AddListener(() => onClick());
            _popup.gameObject.SetActive(true);
        }

        void HidePopup() => _popup.gameObject.SetActive(false);
        void SetStatus(string s) => _statusText.text = s;

        /// <summary>Bir Text'in arkasına aynı hizada yarı saydam çip/arka plan koyar.</summary>
        void AddChip(Text label, Vector2 size, Color color)
        {
            var rt = (RectTransform)label.transform;
            var chip = new GameObject(label.name + "_Chip", typeof(Image));
            var crt = (RectTransform)chip.transform;
            crt.SetParent(rt.parent, false);
            crt.anchorMin = rt.anchorMin; crt.anchorMax = rt.anchorMax;
            crt.pivot = rt.pivot;
            crt.anchoredPosition = rt.anchoredPosition;
            crt.sizeDelta = size;
            chip.GetComponent<Image>().color = color;
            crt.SetSiblingIndex(rt.GetSiblingIndex()); // yazının hemen altına
        }

        // ------------------------------------------------------------------ UI helpers
        RectTransform MakePanel(Transform parent, string name, Vector2 aMin, Vector2 aMax, Color color)
        {
            var go = new GameObject(name, typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = aMin; rt.anchorMax = aMax;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            go.GetComponent<Image>().color = color;
            return rt;
        }

        RectTransform MakeArea(Transform parent, string name, Vector2 anchor, Vector2 pos)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = Vector2.zero;
            return rt;
        }

        RectTransform MakeCardImage(Transform parent, Sprite sprite, float w, float h)
        {
            var go = new GameObject("Card", typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(w, h);
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.preserveAspect = true;
            return rt;
        }

        Text MakeText(Transform parent, string name, Vector2 anchor, Vector2 pos, int size, TextAnchor align)
        {
            var go = new GameObject(name, typeof(Text));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(800, 60);
            var t = go.GetComponent<Text>();
            t.font = _font; t.fontSize = size; t.alignment = align;
            t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        Button MakeButton(Transform parent, string label, Vector2 size, Vector2 anchor, Vector2 pos, int fontSize)
        {
            var go = new GameObject("Btn_" + label, typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            go.GetComponent<Image>().color = new Color(0.16f, 0.45f, 0.28f);
            var t = MakeText(rt, "Label", new Vector2(0.5f, 0.5f), Vector2.zero, fontSize, TextAnchor.MiddleCenter);
            ((RectTransform)t.transform).sizeDelta = size;
            t.text = label;
            return go.GetComponent<Button>();
        }
    }

    /// <summary>Kart GameObject'ine Card verisini iliştirmek için.</summary>
    public class CardRef : MonoBehaviour
    {
        public Bien.Core.Card Card;
    }

    /// <summary>RectTransform'u cihazın safe area'sına oturtur (çentik/köşe kırpması).
    /// NOT: OnRectTransformDimensionsChange içinde anchor değiştirmek layout döngüsü
    /// kilitlenmesi yaratır — o yüzden değişiklik Update'te ucuz karşılaştırmayla izlenir.</summary>
    public class SafeAreaFitter : MonoBehaviour
    {
        private Rect _applied = Rect.zero;

        void Awake() => Apply();

        void Update()
        {
            if (Screen.safeArea != _applied) Apply(); // rotasyon vb. değişimler
        }

        void Apply()
        {
            var rt = transform as RectTransform;
            if (rt == null || Screen.width == 0 || Screen.height == 0) return;
            var sa = Screen.safeArea;
            rt.anchorMin = new Vector2(sa.xMin / Screen.width, sa.yMin / Screen.height);
            rt.anchorMax = new Vector2(sa.xMax / Screen.width, sa.yMax / Screen.height);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            _applied = sa;
        }
    }
}