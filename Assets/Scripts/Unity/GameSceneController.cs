using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
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

        const float CARD_W = 214f, CARD_H = 300f;          // AI kartları (0.58 ölçekle) + yedek
        const float HAND_W = 262f, HAND_H = 367f;          // SENİN kartların — büyük, yelpaze
        const float TRICK_W = 200f, TRICK_H = 280f;        // merkezdeki oynanmış kartlar
        static readonly string[] SeatNames = { "SEN", "BATI", "KUZEY", "DOĞU" };

        // Masa isimleri: koltuk 1-3 her oyun havuzdan rastgele, koltuk 0 kayıtlı oyuncu adı
        // (ana menü yapılınca "bien_player_name" PlayerPrefs anahtarından okunacak).
        static readonly string[] NamePool =
        {
            "Cemil", "Nurten", "Fikret", "Melahat", "İhsan", "Suzan", "Kadir", "Perihan",
            "Orhan", "Leman", "Şükrü", "Nezahat", "Halit", "Müzeyyen", "Ferit", "Saadet"
        };
        readonly string[] _names = { "Sen", "BATI", "KUZEY", "DOĞU" };

        Canvas _canvas;
        TMP_FontAsset _font;      // genel: Resources/bien_tmp (SDF)
        TMP_FontAsset _fancyFont; // plaka:  Resources/bien_tmp_fancy, yoksa _font
        TMP_FontAsset _numFont;   // sayı:   bien_tmp_num veya SpaceGrotesk-Medium SDF, yoksa _font
        static Sprite _roundedSprite; // prosedürel yuvarlak köşe (9-slice) — buton/çip/panel
        static Sprite _feltSprite;    // prosedürel radyal çuha degradesi (yedek zemin)
        Sprite _zoneSprite;           // Resources/bien_zone — sade çerçeve (9-slice)
        Sprite _plateSprite;          // Resources/bien_plate — janjanlı levha (9-slice); yoksa zone
        RectTransform _root, _handArea, _bidPanel, _popup;
        RectTransform _decorRoot;     // masa görseli üstü dekor katmanı (plaka isimleri, koz)
        bool _artTable;               // Resources/bien_table yüklendi mi
        readonly TMP_Text[] _plateTexts = new TMP_Text[4]; // plaka isim yazıları (alt/sol/üst/sağ)
        readonly RectTransform[] _trickSlots = new RectTransform[4];
        readonly TMP_Text[] _seatLabels = new TMP_Text[4];
        readonly TMP_Text[] _bidLabels = new TMP_Text[4];
        readonly RectTransform[] _aiBackAreas = new RectTransform[3]; // seat 1,2,3
        Image _trumpImage; TMP_Text _trumpText, _roundText, _scoreText, _statusText, _bidTotalText;
        GameObject _scoreBox;                                 // sağ üst skor tablosu (art modu)
        readonly TMP_Text[] _scoreNameT = new TMP_Text[4];    // tablo: isim kolonu
        readonly TMP_Text[] _scoreValT = new TMP_Text[4];     // tablo: skor kolonu
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

        // --- Sırası gelen oyuncu vurgusu: isim önce 2 kez yanıp söner, sonra
        // bir sonraki oyuncunun sırası gelene kadar dikkat çeken bir renkte kalır. ---
        static readonly Color NameColorActive = new Color(1f, 0.85f, 0.25f); // altın sarı
        readonly Color[] _turnLabelBaseColor = new Color[4]; // her turda okunan gerçek renk
        Coroutine _turnBlinkCo;
        int _turnSeat = -1;

        /// <summary>_artTable modunda isim plakada (_plateTexts), yoksa köşe etiketinde (_seatLabels).</summary>
        TMP_Text TurnLabel(int seat) => _artTable && _plateTexts[seat] != null ? _plateTexts[seat] : _seatLabels[seat];

        // ------------------------------------------------------------------ setup
        void Awake()
        {
            // TEK FONT DÜZENİ: Resources/bien_tmp her yerde kullanılır.
            // İleride ayrıştırmak istersen: bien_tmp_fancy (plakalar) ve bien_tmp_num
            // (tablo sayıları) adlarıyla ek SDF koyman yeter — varsa otomatik devreye girer.
            _font = Resources.Load<TMP_FontAsset>("bien_tmp");
            _fancyFont = Resources.Load<TMP_FontAsset>("bien_tmp_fancy") ?? _font;
            _numFont = Resources.Load<TMP_FontAsset>("bien_tmp_num") ?? _font;
            if (_roundedSprite == null) _roundedSprite = MakeRoundedSprite(64, 18);
            if (_feltSprite == null) _feltSprite = MakeFeltSprite(256);
            BuildUI();
        }

        // --------------------------------------------------- prosedürel görseller
        /// <summary>Yuvarlak köşeli beyaz 9-slice sprite: buton, çip ve paneller
        /// Image.color ile boyar. Kenar 2px yumuşatmalı (AA).</summary>
        static Sprite MakeRoundedSprite(int size, int radius)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            var px = new Color32[size * size];
            float r = radius;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // köşe merkezine uzaklık (kenar bölgelerinde 0)
                    float dx = Mathf.Max(0, Mathf.Max(r - x, x - (size - 1 - r)));
                    float dy = Mathf.Max(0, Mathf.Max(r - y, y - (size - 1 - r)));
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(r - d + 1f); // 2px yumuşak kenar
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            int b = radius + 6; // 9-slice sınırı köşeyi tamamen kapsasın
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                                 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        }

        /// <summary>Radyal çuha degradesi: merkez aydınlık, kenarlar koyu (vinyet).</summary>
        static Sprite MakeFeltSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            var inner = new Color(0.10f, 0.38f, 0.20f); // merkez: aydınlık çuha
            var outer = new Color(0.03f, 0.17f, 0.09f); // kenar: koyu vinyet
            var px = new Color32[size * size];
            float half = (size - 1) / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float nx = (x - half) / half, ny = (y - half) / half;
                    float t = Mathf.Clamp01(Mathf.Sqrt(nx * nx + ny * ny) / 1.25f);
                    t = t * t * (3f - 2f * t); // smoothstep — yumuşak geçiş
                    px[y * size + x] = Color.Lerp(inner, outer, t);
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        /// <summary>Görsele yumuşak gölge ekler (UI.Shadow).</summary>
        static void AddShadow(Graphic g, float dx = 5f, float dy = -7f, float alpha = 0.35f)
        {
            var sh = g.gameObject.AddComponent<Shadow>();
            sh.effectDistance = new Vector2(dx, dy);
            sh.effectColor = new Color(0f, 0f, 0f, alpha);
        }

        /// <summary>Resources'tan yüklenmiş dokuyu 9-slice sprite'a çevirir (yoksa null).</summary>
        static Sprite SliceSprite(Texture2D t, Vector4 border) => t == null ? null :
            Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f),
                          100f, 0, SpriteMeshType.FullRect, border);

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

            // Masa isimleri: 0 = kayıtlı oyuncu adı (ana menü gelince sorulacak),
            // 1-3 = havuzdan rastgele üç farklı isim (tohuma bağlı — undo tekrarında sabit)
            _names[0] = PlayerPrefs.GetString("bien_player_name", "Sen");
            var picked = NamePool.OrderBy(_ => rng.Next()).Take(3).ToArray();
            for (int s = 1; s <= 3; s++) _names[s] = picked[s - 1];
            for (int s = 0; s < 4; s++)
                if (_plateTexts[s] != null) _plateTexts[s].text = _names[s];
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
                SetBid(s, $"İhale: {n}*  El: 0");
                SetStatus($"{_names[s]} ihalesini {o}→{n} değiştirdi, dağıtıcı kurtarıldı");
                UpdateBidTotal();
            };
            ev.DealerForcedToChange += s => SetStatus($"{_names[s]} ihalesini bozmak zorunda");
            ev.BidTurnStarted += HighlightTurn;
            ev.PlayTurnStarted += HighlightTurn;
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
            if (!_artTable) _bidTotalText.transform.parent.gameObject.SetActive(false);
            _tricksWonLive = new int[4];
            _roundText.text = _artTable
                ? $"TUR {rc.RoundIndex + 1}/16"
                : $"Tur {rc.RoundIndex + 1}/16  •  {rc.CardsPerPlayer} kart" + (rc.HasTrump ? "" : "  •  SANS");
            for (int i = 0; i < 4; i++)
            {
                SetBid(i, "");
                string diff = (debugMode && i > 0 && _seatDiffs != null)
                    ? $" [{_seatDiffs[i]}]" : "";
                _seatLabels[i].text = _names[i] + diff + (i == dealer ? "  (dağıtan)" : "");
                if (_plateTexts[i] != null) // plakada isim + dağıtan işareti (♦)
                    _plateTexts[i].text = _names[i] + (i == dealer ? " ♦" : "");
                _turnLabelBaseColor[i] = TurnLabel(i).color; // sıra vurgusu bunu geri yükleyecek
            }
            if (_turnBlinkCo != null) { StopCoroutine(_turnBlinkCo); _turnBlinkCo = null; }
            _turnSeat = -1;
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
            SetStatus($"{_names[_curDealer]} dağıtıyor...");
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
                _trumpText.text = _artTable ? "" : "KOZ"; // görselde "AÇILAN KOZ" baskılı
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
            SetBid(seat, $"İhale: {bid}  El: 0");
            SetStatus($"{_names[seat]}: {bid} dedi");
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
            if (!_artTable) _bidTotalText.transform.parent.gameObject.SetActive(true);
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
            // Oynanma sırasına göre üst üste: bu koltuğun slotunu blok içinde en üste al
            // (slotlar ardışık kardeşler; blok dışına çıkmaz, popup/el hep üstte kalır)
            int maxIdx = 0;
            foreach (var sl in _trickSlots) maxIdx = Mathf.Max(maxIdx, sl.GetSiblingIndex());
            _trickSlots[seat].SetSiblingIndex(maxIdx);

            var tc = MakeCardImage(_trickSlots[seat], CardSprites.Get(card), TRICK_W, TRICK_H);
            _trickCards.Add(tc.gameObject);
            // Janjan: kart dönerek ve büyükten küçülerek gelir, sonda TAM DÜŞEY oturur
            if (_fast) tc.anchoredPosition = Vector2.zero;
            else
            {
                Vector2 slideFrom = SeatFxPos(seat) - _slotPos[seat];
                float dir = seat == 1 ? -1f : seat == 3 ? 1f : seat == 2 ? 0.7f : -0.7f;
                float spin = dir * UnityEngine.Random.Range(160f, 260f); // geliş yönüne göre dönüş
                tc.anchoredPosition = slideFrom;
                StartCoroutine(FlySpin(tc, slideFrom, Vector2.zero, 0.34f, spin, 0f, 1.35f));
            }
        }

        void OnTrickWon(int winner, IReadOnlyList<Card> trick)
        {
            _tricksWonLive[winner]++;
            SetBid(winner, $"İhale: {_lastBids[winner]}  El: {_tricksWonLive[winner]}");
            SetStatus($"Eli {_names[winner]} aldı");
            if (_fast) { ClearTrick(); return; }
            var tcs = new TaskCompletionSource<bool>();
            _gate = tcs.Task;
            StartCoroutine(CollectTrick(winner, tcs));
        }

        /// <summary>El bitişi: kartlar görünür kalır → dönerek merkezde deste olur →
        /// eli alanın yönüne uçup sönerek kaybolur. Kazananı gözle takip ettirir.</summary>
        System.Collections.IEnumerator CollectTrick(int winner, TaskCompletionSource<bool> tcs)
        {
            yield return new WaitForSeconds(0.45f); // masadaki eli gör

            var cards = new System.Collections.Generic.List<RectTransform>();
            var starts = new System.Collections.Generic.List<Vector2>();
            var mids = new System.Collections.Generic.List<Vector2>();
            var cgs = new System.Collections.Generic.List<CanvasGroup>();
            foreach (var go in _trickCards)
            {
                if (go == null) continue;
                var rt = (RectTransform)go.transform;
                int slot = 0;
                for (int s = 0; s < 4; s++) if (rt.parent == _trickSlots[s]) slot = s;
                cards.Add(rt);
                starts.Add(rt.anchoredPosition);
                mids.Add(-_slotPos[slot]); // ekran merkezine (slot ofsetini sıfırla)
                var cg = go.GetComponent<CanvasGroup>();
                if (cg == null) cg = go.AddComponent<CanvasGroup>(); // ?? kullanma: Unity sahte-null tuzağı
                cg.blocksRaycasts = false;
                cgs.Add(cg);
            }

            // Faz 1: dönerek merkezde toplan (yarım tur)
            float t = 0; const float D1 = 0.30f;
            while (t < D1)
            {
                t += Time.deltaTime;
                float k = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / D1), 2f);
                for (int i = 0; i < cards.Count; i++)
                {
                    if (cards[i] == null) continue;
                    cards[i].anchoredPosition = Vector2.LerpUnclamped(starts[i], mids[i], k);
                    cards[i].localRotation = Quaternion.Euler(0, 0, 180f * k);
                    float sc = Mathf.Lerp(1f, 0.88f, k);
                    cards[i].localScale = new Vector3(sc, sc, 1f);
                }
                yield return null;
            }

            // Faz 2: kazananın yönüne uç, dönmeye devam et, küçülerek sön
            Vector2 fly = SeatFxPos(winner) * 1.4f;
            t = 0; const float D2 = 0.34f;
            while (t < D2)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / D2);
                float ke = k * k; // ease-in: hızlanarak gider
                for (int i = 0; i < cards.Count; i++)
                {
                    if (cards[i] == null) continue;
                    cards[i].anchoredPosition = Vector2.LerpUnclamped(mids[i], fly + mids[i], ke);
                    cards[i].localRotation = Quaternion.Euler(0, 0, 180f + 150f * k);
                    float sc = Mathf.Lerp(0.88f, 0.45f, ke);
                    cards[i].localScale = new Vector3(sc, sc, 1f);
                    cgs[i].alpha = 1f - ke;
                }
                yield return null;
            }

            ClearTrick();
            tcs.TrySetResult(true);
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
                $"OYUN BİTTİ — {_names[w]} KAZANDI!");
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

            _root = MakePanel(cgo.transform, "Root", Vector2.zero, Vector2.one, Color.white);
            _root.GetComponent<Image>().sprite = _feltSprite; // yedek zemin (masa görselleri yoksa)

            // ---- Masa görseli: SADECE bien_table (2532x1170). Yoksa prosedürel düz çuha.
            // bien_zone yalnız çerçeveli kutuların deseni için kullanılır.
            var tableTex = Resources.Load<Texture2D>("bien_table");
            _artTable = tableTex != null;
            _zoneSprite = SliceSprite(Resources.Load<Texture2D>("bien_zone"), new Vector4(48, 48, 48, 48));
            var plateTex = Resources.Load<Texture2D>("bien_plate"); // janjanlı levha (köşe süsleri ~70px içinde)
            _plateSprite = plateTex != null ? SliceSprite(plateTex, new Vector4(70, 70, 70, 70)) : _zoneSprite;

            if (_artTable)
            {
                // Masa görseli + dekoru SAFE-AREA içinde: çentik görselin DIŞINDAKİ koyu
                // ahşap marja düşer, kenar plakaları hiçbir cihazda çentik altında kalmaz.
                // Editor'de safe = tam ekran olduğundan görünüm birebir aynı kalır.
                var rootImg = _root.GetComponent<Image>();
                rootImg.sprite = null;
                rootImg.color = new Color(0.07f, 0.045f, 0.025f); // marj: koyu ahşap tonu

                // Masa görseli TAM EKRAN (safe-area'ya sokulmaz — plakalar masanın içinde,
                // çentik olsa olsa ahşap kenara biner). Oyun öğeleri _safe'te kalır.
                var artSafeGo = new GameObject("ArtFull", typeof(RectTransform));
                var artSafe = (RectTransform)artSafeGo.transform;
                artSafe.SetParent(_root, false);
                artSafe.anchorMin = Vector2.zero; artSafe.anchorMax = Vector2.one;
                artSafe.offsetMin = artSafe.offsetMax = Vector2.zero;

                var tgo = new GameObject("TableArt", typeof(RawImage));
                var trt = (RectTransform)tgo.transform;
                trt.SetParent(artSafe, false);
                trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
                trt.offsetMin = trt.offsetMax = Vector2.zero;
                var tri = tgo.GetComponent<RawImage>();
                tri.texture = tableTex; tri.raycastTarget = false;

                // Dekor katmanı: plaka isimleri + koz göstergesi buraya oturur (görselle birlikte kayar)
                var dgo = new GameObject("Decor", typeof(RectTransform));
                _decorRoot = (RectTransform)dgo.transform;
                _decorRoot.SetParent(artSafe, false);
                _decorRoot.anchorMin = Vector2.zero; _decorRoot.anchorMax = Vector2.one;
                _decorRoot.offsetMin = _decorRoot.offsetMax = Vector2.zero;

                // İsimler artık kenar plakalarında değil: kenar → kartlar → isim → ihale
                // düzeninde, merkeze doğru pill'lerde (aşağıda _safe kurulunca eklenir).
                // Tulga PNG'deki isim bantlarını içeri taşıyınca pill'ler o bantlara oturtulacak.
            }
            // Çentik/safe-area: zemin tam ekran, içerik güvenli alanda
            var safeGo = new GameObject("SafeArea", typeof(RectTransform), typeof(SafeAreaFitter));
            _safe = (RectTransform)safeGo.transform;
            _safe.SetParent(_root, false);

            if (_artTable)
            {
                // İsimler: KODLA çizilen çerçeveli levhalar (bien_plate varsa o desen,
                // yoksa bien_zone). Konumlar son ölçümden; Tulga PNG'de levha bulundurmayacak.
                _plateTexts[2] = MakeFramedLabel(_decorRoot, new Vector2(1249f / 2532f, 1f), new Vector2(0, -233), new Vector2(330, 80), 44, 0, false); // KUZEY
                _plateTexts[0] = MakeFramedLabel(_decorRoot, new Vector2(1249f / 2532f, 0f), new Vector2(0, 232), new Vector2(330, 80), 44, 0, false);  // SEN
                _plateTexts[1] = MakeFramedLabel(_decorRoot, new Vector2(453f / 2532f, 1f), new Vector2(0, -590), new Vector2(330, 80), 44, 0, false);  // BATI
                _plateTexts[3] = MakeFramedLabel(_decorRoot, new Vector2(2067f / 2532f, 1f), new Vector2(0, -590), new Vector2(330, 80), 44, 0, false); // DOĞU

                // İhale çipleri: levhaların iç yanında (kuzey altına, güney üstüne, yanlar altına)
                _bidLabels[2] = MakeFramedLabel(_decorRoot, new Vector2(1249f / 2532f, 1f), new Vector2(0, -326), new Vector2(430, 68), 44, 0, true);
                _bidLabels[0] = MakeFramedLabel(_decorRoot, new Vector2(1249f / 2532f, 0f), new Vector2(0, 325), new Vector2(430, 68), 44, 0, true);
                _bidLabels[1] = MakeFramedLabel(_decorRoot, new Vector2(453f / 2532f, 1f), new Vector2(0, -682), new Vector2(430, 68), 44, 0, true);
                _bidLabels[3] = MakeFramedLabel(_decorRoot, new Vector2(2067f / 2532f, 1f), new Vector2(0, -682), new Vector2(430, 68), 44, 0, true);
            }

            if (!_artTable)
            {
                // Yedek: masa merkezini belli eden hafif koyu, yuvarlak zemin
                var center = new GameObject("TrickZone", typeof(Image));
                var crt0 = (RectTransform)center.transform;
                crt0.SetParent(_safe, false);
                crt0.anchorMin = crt0.anchorMax = new Vector2(0.5f, 0.5f);
                crt0.anchoredPosition = new Vector2(0, 0);
                crt0.sizeDelta = new Vector2(1150, 620);
                var cimg = center.GetComponent<Image>();
                cimg.sprite = _roundedSprite; cimg.type = Image.Type.Sliced;
                cimg.color = new Color(0f, 0f, 0f, 0.16f);
                cimg.raycastTarget = false;
            }

            // INFO KUTUSU: Toplam + TUR + oyun mesajı TEK janjanlı çerçevede (sol sütun)
            if (_artTable)
            {
                var ibox = new GameObject("InfoBox", typeof(Image));
                var ibr = (RectTransform)ibox.transform;
                ibr.SetParent(_decorRoot, false);
                ibr.anchorMin = ibr.anchorMax = new Vector2(650f / 2532f, 1f); // koz ile üst levha arası
                ibr.pivot = new Vector2(0f, 1f);            // sol-üst köşeden büyür
                ibr.anchoredPosition = new Vector2(0, -95);
                ibr.sizeDelta = new Vector2(370, 150); // iki satır: TUR + Toplam
                var ibi = ibox.GetComponent<Image>();
                ibi.sprite = _plateSprite != null ? _plateSprite : _roundedSprite;
                ibi.type = Image.Type.Sliced;
                ibi.color = _plateSprite != null ? Color.white : new Color(0f, 0f, 0f, 0.45f);
                ibi.raycastTarget = false;

                // İki satır: TUR + Toplam. Mesaj satırı KALDIRILDI (sıra göstergesi ayrıca yapılacak);
                // SetStatus çağrıları kırılmasın diye gizli bir metne yazılır.
                _roundText = MakeText(ibr, "L1", new Vector2(0.5f, 1f), new Vector2(0, -42), 34, TextAnchor.MiddleCenter);
                _bidTotalText = MakeText(ibr, "L2", new Vector2(0.5f, 1f), new Vector2(0, -106), 35, TextAnchor.MiddleCenter);
                foreach (var l in new[] { _bidTotalText, _roundText })
                {
                    ((RectTransform)l.transform).sizeDelta = new Vector2(330, 60);
                    l.overflowMode = TextOverflowModes.Ellipsis;
                    if (_fancyFont != null) l.font = _fancyFont;
                }
                _statusText = MakeText(_decorRoot, "StatusHidden", new Vector2(0.5f, 0f), new Vector2(0, -200), 30, TextAnchor.MiddleCenter);
                _statusText.gameObject.SetActive(false);
            }
            else
            {
                _roundText = MakeText(_safe, "Round", new Vector2(1f, 1f), new Vector2(-620, -45), 36, TextAnchor.MiddleCenter);
                ((RectTransform)_roundText.transform).sizeDelta = new Vector2(460, 56);
                AddChip(_roundText, new Vector2(470, 58), new Color(0f, 0f, 0f, 0.35f));
                _statusText = MakeText(_safe, "Status", new Vector2(0.5f, 0f), new Vector2(0, 368), 32, TextAnchor.MiddleCenter);
            }
            if (!_artTable) _statusText.color = new Color(1f, 0.92f, 0.6f);

            // Koz göstergesi (sol üst) — masa görselindeki yuvaya hizalı.
            // Görsel tam ekrana GERİLDİĞİ için yuvanın x'i oransal çapayla izlenir
            // (farklı en-boyda görsel yatay esner; 281/2532 oranı hep yuvada kalır).
            var trumpParent = _decorRoot != null ? (Transform)_decorRoot : _safe;
            var trumpAnchor = _artTable ? new Vector2(420f / 2532f, 1f) : new Vector2(0f, 1f); // yuva merkezi (final)
            var trumpGo = MakeCardImage(trumpParent, null,
                _artTable ? 155f : CARD_W * 0.72f, _artTable ? 217f : CARD_H * 0.72f); // yuva içi (final ölçüm)
            trumpGo.anchorMin = trumpGo.anchorMax = trumpAnchor;
            trumpGo.anchoredPosition = _artTable ? new Vector2(0, -229) : new Vector2(240, -240);
            _trumpImage = trumpGo.GetComponent<Image>();
            // Etiket: görselde "AÇILAN KOZ" zaten baskılı → art modunda sadece SANS yazısı,
            // yuvanın ortasında; diğer modlarda yuvanın altında KOZ/SANS.
            _trumpText = MakeText(trumpParent, "TrumpLbl", trumpAnchor,
                _artTable ? new Vector2(0, -229) : new Vector2(240, -388), _artTable ? 32 : 30, TextAnchor.MiddleCenter);
            ((RectTransform)_trumpText.transform).sizeDelta = new Vector2(200, 40);
            if (_artTable) { _trumpText.fontStyle = FontStyles.Normal; _trumpText.color = new Color(0.95f, 0.87f, 0.60f); }

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

            // Toplam ihale: art modunda InfoBox içinde (yukarıda kuruldu); yedekte ayrı çerçeve
            if (!_artTable)
                _bidTotalText = MakeFramedLabel(_safe, new Vector2(0f, 1f), new Vector2(620, -240), new Vector2(430, 64), 38, 0, true);

            // Skor (sağ üst)
            // Genel skor: sağ üstte, TAMAMEN ekran içinde, çerçeveli (eski hali yarıya kadar
            // ekran dışındaydı — sadece son iki satır görünüyordu)
            if (_artTable)
            {
                // SKOR: iki kolonlu (isim | puan), ince ızgara çizgili mini tablo
                _scoreBox = new GameObject("ScoreBox", typeof(Image));
                var sbr = (RectTransform)_scoreBox.transform;
                sbr.SetParent(_safe, false);
                sbr.anchorMin = sbr.anchorMax = new Vector2(1f, 1f);
                sbr.anchoredPosition = new Vector2(-395, -255);
                sbr.sizeDelta = new Vector2(400, 342);
                var sbi = _scoreBox.GetComponent<Image>();
                sbi.sprite = _plateSprite != null ? _plateSprite : _roundedSprite;
                sbi.type = Image.Type.Sliced;
                sbi.color = _plateSprite != null ? Color.white : new Color(0f, 0f, 0f, 0.45f);
                sbi.raycastTarget = false;

                void SLine(float x, float y, float w, float h)
                {
                    var lg = new GameObject("Ln", typeof(Image));
                    var lr = (RectTransform)lg.transform;
                    lr.SetParent(sbr, false);
                    lr.anchorMin = lr.anchorMax = new Vector2(0.5f, 1f);
                    lr.anchoredPosition = new Vector2(x, y);
                    lr.sizeDelta = new Vector2(w, h);
                    var li = lg.GetComponent<Image>();
                    li.color = new Color(1f, 1f, 1f, 0.16f);
                    li.raycastTarget = false;
                }

                var hd = MakeText(sbr, "H", new Vector2(0.5f, 1f), new Vector2(0, -37), 34, TextAnchor.MiddleCenter);
                hd.text = "PUANLAR"; hd.fontStyle = FontStyles.Normal;
                hd.color = new Color(1f, 0.9f, 0.45f); if (_fancyFont != null) hd.font = _fancyFont;
                SLine(0, -64, 352, 2);                    // başlık altı
                for (int i = 0; i < 4; i++)
                {
                    float y = -97 - i * 61;
                    _scoreNameT[i] = MakeText(sbr, $"N{i}", new Vector2(0.5f, 1f), new Vector2(-84, y), 33, TextAnchor.MiddleLeft);
                    ((RectTransform)_scoreNameT[i].transform).sizeDelta = new Vector2(190, 56);
                    _scoreNameT[i].color = new Color(0.95f, 0.87f, 0.60f);
                    _scoreNameT[i].fontStyle = FontStyles.Normal;
                    _scoreValT[i] = MakeText(sbr, $"V{i}", new Vector2(0.5f, 1f), new Vector2(110, y), 34, TextAnchor.MiddleCenter);
                    ((RectTransform)_scoreValT[i].transform).sizeDelta = new Vector2(120, 56);
                    _scoreValT[i].fontStyle = FontStyles.Normal;
                    if (_numFont != null) _scoreValT[i].font = _numFont;
                    if (i < 3) SLine(0, y - 30.5f, 352, 1.5f); // satır ayracı
                }
                SLine(46, -188, 1.5f, 248);               // kolon ayracı (başlık altından tabana)
                _scoreBox.SetActive(false);               // ilk skora kadar gizli
            }
            else
            {
                _scoreText = MakeText(_safe, "Score", new Vector2(1f, 1f), new Vector2(-190, -230), 28, TextAnchor.UpperLeft);
                ((RectTransform)_scoreText.transform).sizeDelta = new Vector2(300, 300);
                _scoreText.text = "";
            }

            // Koltuk etiketleri + ihale çipleri + AI arka alanları
            // seat1 BATI (sol), seat2 KUZEY (üst), seat3 DOĞU (sağ)
            // İhale çipleri BOŞKEN GİZLİ (SetBid yönetir). Art modunda isim tahtası stilinde,
            // isimlerin dibinde: kuzey üst plakanın altında, batı/doğu plakanın iç yanında DÜŞEY.
            if (!_artTable)
            {
                _bidLabels[0] = MakeBidChip(new Vector2(0f, 0f), new Vector2(220, 205));
                _bidLabels[1] = MakeBidChip(new Vector2(0f, 0.5f), new Vector2(420, -85));
                _bidLabels[2] = MakeBidChip(new Vector2(0.5f, 1f), new Vector2(340, -78));
                _bidLabels[3] = MakeBidChip(new Vector2(1f, 0.5f), new Vector2(-420, -85));
            }

            _seatLabels[0] = MakeText(_safe, "L0", new Vector2(0f, 0f), new Vector2(190, 250), 32, TextAnchor.MiddleCenter);
            _seatLabels[1] = MakeText(_safe, "L1", new Vector2(0f, 0.5f), new Vector2(340, 175), 32, TextAnchor.MiddleCenter);
            _seatLabels[2] = MakeText(_safe, "L2", new Vector2(0.5f, 1f), new Vector2(0, -100), 32, TextAnchor.MiddleCenter);
            _seatLabels[3] = MakeText(_safe, "L3", new Vector2(1f, 0.5f), new Vector2(-340, 175), 32, TextAnchor.MiddleCenter);
            foreach (var t in _seatLabels)
            {
                if (_artTable) { t.gameObject.SetActive(false); continue; } // isimler plakalarda
                t.fontStyle = FontStyles.Normal;
                AddChip(t, new Vector2(250, 48), new Color(0f, 0f, 0f, 0.28f));
            }

            // BATI/DOĞU elleri kenara yaslı DÜŞEY sütun, KUZEY yatay sıra
            // AI kartları kenara yarı gömük (art); isim ve ihale iç yanlarında
            _aiBackAreas[0] = MakeArea(_safe, "BacksW", new Vector2(0f, 0.5f), new Vector2(_artTable ? 45 : 300, -10));
            _aiBackAreas[1] = MakeArea(_safe, "BacksN", new Vector2(0.5f, 1f), new Vector2(0, _artTable ? -90 : -275));
            _aiBackAreas[2] = MakeArea(_safe, "BacksE", new Vector2(1f, 0.5f), new Vector2(_artTable ? -45 : -300, -10));

            // El alanları (merkez etrafı) — sırayla alt, sol, üst, sağ; iyice iç içe pile
            _slotPos = new Vector2[] { new(0, -90), new(-115, 0), new(0, 88), new(115, 0) };
            for (int i = 0; i < 4; i++)
            {
                _trickSlots[i] = MakeArea(_safe, $"Trick{i}", new Vector2(0.5f, 0.5f), _slotPos[i]);
            }

            // Uçan kartlar için katman (dağıtım animasyonu burada oynar)
            _fxLayer = MakeArea(_safe, "FX", new Vector2(0.5f, 0.5f), Vector2.zero);

            // İnsan eli (alt)
            // El: kartların ~yarısı ekranda ("elimde tutuyorum" hissi); üstünde isim, onun üstünde ihale
            _handArea = MakeArea(_safe, "Hand", new Vector2(0.5f, 0f), new Vector2(0, _artTable ? -35 : 185));


            // İhale paneli
            _bidPanel = MakePanel(_safe, "BidPanel", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Color.black); // tam opak
            _bidPanel.sizeDelta = new Vector2(980, 620);
            _bidPanel.anchoredPosition = new Vector2(0, 80); // el kartlarının üstünde kalsın
            var bpImg = _bidPanel.GetComponent<Image>();
            bpImg.sprite = _roundedSprite; bpImg.type = Image.Type.Sliced;
            _bidPanel.gameObject.SetActive(false);

            // Debug paneli (sol alt)

            // Popup
            _popup = MakePanel(_safe, "Popup", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Color(0f, 0f, 0f, 0.92f));
            _popup.sizeDelta = new Vector2(980, 900);
            var puImg = _popup.GetComponent<Image>();
            puImg.sprite = _roundedSprite; puImg.type = Image.Type.Sliced;
            AddShadow(puImg, 0f, -10f, 0.45f);
            _popup.gameObject.SetActive(false);
        }

        /// <summary>İhale seçimi: masanın ortasında tek sıra KARE butonlar (0..N).
        /// Kare boyutu satıra sığacak şekilde hesaplanır — 13'lük turda 14 kare rahat sığar.</summary>
        void BuildBidButtons(int maxBid, int? forbidden, Action<int> onPick, Action onKeep)
        {
            foreach (Transform c in _bidPanel) Destroy(c.gameObject);

            int count = maxBid + 1;
            bool revision = onKeep != null;
            const float GAP = 14f;
            // Tulga: 7'den çok seçenekte kareler tek satıra sıkışmasın, ikinci satıra taşsın.
            int cols = count > 7 ? Mathf.CeilToInt(count / 2f) : count;
            int rows = count > 7 ? 2 : 1;
            float size = Mathf.Min(130f, (2080f - (cols - 1) * GAP) / cols);
            float rowW = cols * size + (cols - 1) * GAP;

            _bidPanel.sizeDelta = new Vector2(Mathf.Max(rowW + 90, 760),
                                              rows * size + (rows - 1) * GAP + 152 + (revision ? 104 : 0));
            _bidPanel.anchoredPosition = new Vector2(0, 70);

            if (_zoneSprite != null) // standart altın çerçeve (kenarlara oturur)
            {
                var fgo = new GameObject("Frame", typeof(Image));
                var frt = (RectTransform)fgo.transform;
                frt.SetParent(_bidPanel, false);
                frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
                frt.offsetMin = frt.offsetMax = Vector2.zero;
                var fim = fgo.GetComponent<Image>();
                fim.sprite = _plateSprite; fim.type = Image.Type.Sliced;
                fim.raycastTarget = false;
            }

            var q = MakeText(_bidPanel, "Q", new Vector2(0.5f, 1f), new Vector2(0, -50), 41, TextAnchor.MiddleCenter);
            q.text = revision ? "Yeni ihalen? (veya değiştirme)" : "Kaç el alırsın?";
            q.fontStyle = FontStyles.Normal;

            float y0 = -96 - size / 2;
            for (int v = 0; v <= maxBid; v++)
            {
                int bid = v;
                bool blocked = forbidden.HasValue && v == forbidden.Value;
                int r = v / cols, c2 = v % cols;
                int itemsInRow = (r == rows - 1) ? (count - cols * (rows - 1)) : cols; // son satır kısa kalabilir
                float x = (c2 - (itemsInRow - 1) / 2f) * (size + GAP);
                float y = y0 - r * (size + GAP);
                var b = MakeButton(_bidPanel, v.ToString(), new Vector2(size, size),
                                   new Vector2(0.5f, 1f), new Vector2(x, y),
                                   Mathf.RoundToInt(size * 0.42f));
                b.GetComponent<Image>().color = new Color(0.12f, 0.18f, 0.40f); // lacivert kare, beyaz rakam
                if (blocked)
                {
                    b.interactable = false;
                    b.GetComponent<Image>().color = new Color(0.45f, 0.18f, 0.18f); // yasak: kırmızı, pasif
                }
                else b.onClick.AddListener(() => { _bidPanel.gameObject.SetActive(false); onPick(bid); });
            }

            if (revision)
            {
                var keep = MakeButton(_bidPanel, "DEĞİŞTİRME", new Vector2(400, 78),
                                      new Vector2(0.5f, 0f), new Vector2(0, 60), 30);
                keep.GetComponent<Image>().color = new Color(0.25f, 0.35f, 0.55f);
                keep.onClick.AddListener(() => { _bidPanel.gameObject.SetActive(false); onKeep(); });
            }
        }

        // ------------------------------------------------------------------ rendering
        void RenderHumanHand(IReadOnlyList<Card> hand, bool interactable)
        {
            foreach (var g in _handCards) Destroy(g);
            _handCards.Clear();
            var sorted = hand.OrderBy(c => c.Suit).ThenByDescending(c => c.Rank).ToList();
            foreach (var card in sorted)
            {
                var rt = MakeCardImage(_handArea, CardSprites.Get(card), HAND_W, HAND_H);
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
            // El sayısından bağımsız yelpaze: kartlar HER ZAMAN üst üste biner,
            // çok kartta toplam genişlik sığacak kadar sıkışır.
            float step = HAND_W * 0.62f;
            if (n > 1) step = Mathf.Min(step, (2100f - HAND_W) / (n - 1));
            float total = HAND_W + (n - 1) * step;
            for (int i = 0; i < n; i++)
            {
                var rt = (RectTransform)_handCards[i].transform;
                rt.anchoredPosition = new Vector2(-total / 2 + HAND_W / 2 + i * step, rt.anchoredPosition.y);
                rt.SetSiblingIndex(i);
            }
        }

        /// <summary>AI kartı yerleştirme: KUZEY yatay sıra, BATI/DOĞU yan yatık düşey sütun.</summary>
        RectTransform PlaceAiCard(int seat, RectTransform area, Sprite sprite, int i, int count)
        {
            float w = CARD_W * 0.58f, h = CARD_H * 0.58f;
            var rt = MakeCardImage(area, sprite, w, h);
            if (seat == 2) // KUZEY: yatay
            {
                float step = Mathf.Min(40f, 560f / Mathf.Max(1, count));
                rt.anchoredPosition = new Vector2((i - (count - 1) / 2f) * step, 0);
            }
            else           // BATI/DOĞU: düşey, kartlar yan yatık (üstten alta)
            {
                float step = count > 1 ? Mathf.Min(46f, (640f - w) / (count - 1)) : 0f;
                rt.localRotation = Quaternion.Euler(0, 0, seat == 1 ? -90f : 90f);
                rt.anchoredPosition = new Vector2(0, (count - 1) / 2f * step - i * step);
            }
            return rt;
        }

        void RenderAiBacks(int seat, int count)
        {
            var area = _aiBackAreas[seat - 1];
            foreach (Transform c in area) Destroy(c.gameObject);
            for (int i = 0; i < count; i++)
                PlaceAiCard(seat, area, CardSprites.Back, i, count);
        }

        void RenderAiFaces(int seat, IReadOnlyList<Card> hand)
        {
            var area = _aiBackAreas[seat - 1];
            foreach (Transform c in area) Destroy(c.gameObject);
            var sorted = hand.OrderBy(c => c.Suit).ThenByDescending(c => c.Rank).ToList();
            for (int i = 0; i < sorted.Count; i++)
                PlaceAiCard(seat, area, CardSprites.Get(sorted[i]), i, sorted.Count)
                    .gameObject.AddComponent<CardRef>().Card = sorted[i];
        }

        void DebugLine(string s) => AiLogger.Write(s);

        /// <summary>Kart uçuşu: konum ease-out, dönüş ve ölçek birlikte söner; sonda 'rest' açısında kalır.</summary>
        System.Collections.IEnumerator FlySpin(RectTransform rt, Vector2 from, Vector2 to, float dur,
                                               float fromRot, float restRot, float fromScale)
        {
            float t = 0;
            while (t < dur)
            {
                if (rt == null) yield break;
                t += Time.deltaTime;
                float k = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / dur), 3f); // ease-out cubic
                rt.anchoredPosition = Vector2.LerpUnclamped(from, to, k);
                rt.localRotation = Quaternion.Euler(0, 0, Mathf.LerpUnclamped(fromRot, restRot, k));
                float s = Mathf.LerpUnclamped(fromScale, 1f, k);
                rt.localScale = new Vector3(s, s, 1f);
                yield return null;
            }
            if (rt != null)
            {
                rt.anchoredPosition = to;
                rt.localRotation = Quaternion.Euler(0, 0, restRot);
                rt.localScale = Vector3.one;
            }
        }

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
            // DİKKAT: motor TotalScores'u tur-sonu olayı bittikten SONRA günceller —
            // oradan okuyunca tablo bir el geriden gelir. Toplamlar _history'den alınır.
            var totals = new int[4];
            foreach (var rr in _history)
                for (int i = 0; i < 4; i++) totals[i] += rr.Scores[i];

            if (_scoreBox != null)
            {
                for (int i = 0; i < 4; i++)
                {
                    _scoreNameT[i].text = _names[i];
                    _scoreValT[i].text = totals[i].ToString();
                }
                _scoreBox.SetActive(true); // ilk puanlarla birlikte görünür
                return;
            }
            _scoreText.text = "PUANLAR\n" + string.Join("\n",
                totals.Select((s, i) => $"{_names[i]}  {s}"));
        }

        /// <summary>4 sütun × 16 tur + toplam satırı. Batan: kırmızı X, çıkan: o tur aldığı puan.
        /// Sıkı sütunlar + ince ızgara çizgileri.</summary>
        void ShowScoreTable(string btnLabel, Action onClick, string titleOverride = null)
        {
            foreach (Transform c in _popup) Destroy(c.gameObject);
            _popup.sizeDelta = new Vector2(1080, 1140);
            _popup.GetComponent<Image>().color = Color.black; // tam opak

            var t = MakeText(_popup, "T", new Vector2(0.5f, 1f), new Vector2(0, -46), 50, TextAnchor.MiddleCenter);
            t.text = titleOverride ?? "PUAN TABLOSU";
            t.fontStyle = FontStyles.Normal;

            float[] colX = { -420, -255, -45, 165, 375 }; // El kolonu dar (120), oyuncular 210
            const float ROW_H = 52;
            const float TOP_Y = -152;                    // ilk satır merkezi
            var rounds = GameStructure.BuildRounds();

            void Line(float x, float y, float w, float h, float a)
            {
                var go = new GameObject("Ln", typeof(Image));
                var lrt = (RectTransform)go.transform;
                lrt.SetParent(_popup, false);
                lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 1f);
                lrt.anchoredPosition = new Vector2(x, y);
                lrt.sizeDelta = new Vector2(w, h);
                var im = go.GetComponent<Image>();
                im.color = new Color(1f, 1f, 1f, a);
                im.raycastTarget = false;
            }

            // Başlık satırı
            for (int p = 0; p < 4; p++)
            {
                var h = MakeText(_popup, $"H{p}", new Vector2(0.5f, 1f), new Vector2(colX[p + 1], -102), 40, TextAnchor.MiddleCenter);
                h.text = _names[p];
                h.fontStyle = FontStyles.Normal;
                h.color = new Color(1f, 0.9f, 0.45f);
            }
            var hl = MakeText(_popup, "HL", new Vector2(0.5f, 1f), new Vector2(colX[0], -102), 34, TextAnchor.MiddleCenter);
            hl.text = "El";
            hl.color = new Color(0.7f, 0.8f, 0.75f);

            // Izgara: başlık altı + satır ayraçları. Çizgiler en az 2px — 1px çizgi
            // telefonda canvas ölçeğiyle alt-piksele düşüp kayboluyordu (10. satır sonrası vakası).
            float gridTop = TOP_Y + ROW_H / 2f;                       // ilk bandın üstü — bantlar eşit
            float gridBot = TOP_Y - (rounds.Count - 1) * ROW_H - ROW_H / 2f;
            Line(0, gridTop, 960, 3, 0.32f);
            for (int i = 0; i < rounds.Count; i++)
                Line(0, TOP_Y - ROW_H / 2f - i * ROW_H, 960, i == rounds.Count - 1 ? 3 : 2,
                     i == rounds.Count - 1 ? 0.32f : 0.13f);
            for (int v = 0; v < 4; v++) // dikey ayraçlar: sütun ortaları
                Line(-360 + v * 210, (gridTop + gridBot) / 2f, 2f, gridTop - gridBot, 0.13f);

            // Tur satırları
            for (int i = 0; i < rounds.Count; i++)
            {
                float y = TOP_Y - i * ROW_H;
                var lbl = MakeText(_popup, $"R{i}", new Vector2(0.5f, 1f), new Vector2(colX[0], y), 36, TextAnchor.MiddleCenter);
                lbl.text = rounds[i].CardsPerPlayer.ToString() + (rounds[i].HasTrump ? "" : "*");
                lbl.color = new Color(0.7f, 0.8f, 0.75f);
                if (_numFont != null) lbl.font = _numFont;

                if (i >= _history.Count) continue;
                var rr = _history[i];
                for (int p = 0; p < 4; p++)
                {
                    var cell = MakeText(_popup, $"C{i}_{p}", new Vector2(0.5f, 1f), new Vector2(colX[p + 1], y), 40, TextAnchor.MiddleCenter);
                    if (_numFont != null) cell.font = _numFont;
                    bool made = rr.Bids[p] == rr.TricksWon[p];
                    if (made) { cell.text = rr.Scores[p].ToString(); cell.color = Color.white; }
                    else { cell.text = "X"; cell.fontStyle = FontStyles.Normal; cell.color = new Color(1f, 0.35f, 0.3f); }
                }
            }

            // Toplamlar
            float ty = TOP_Y - rounds.Count * ROW_H - 18;
            var tl = MakeText(_popup, "TL", new Vector2(0.5f, 1f), new Vector2(colX[0], ty), 38, TextAnchor.MiddleCenter);
            tl.text = "TOP";
            tl.fontStyle = FontStyles.Normal;
            tl.color = new Color(1f, 0.85f, 0.25f);
            for (int p = 0; p < 4; p++)
            {
                int sum = 0;
                foreach (var rr in _history) sum += rr.Scores[p];
                var tot = MakeText(_popup, $"T{p}", new Vector2(0.5f, 1f), new Vector2(colX[p + 1], ty), 46, TextAnchor.MiddleCenter);
                if (_numFont != null) tot.font = _numFont;
                tot.text = sum.ToString();
                tot.fontStyle = FontStyles.Normal;
                tot.color = new Color(1f, 0.85f, 0.25f);
            }

            var btn = MakeButton(_popup, btnLabel, new Vector2(380, 82), new Vector2(0.5f, 0f), new Vector2(0, 50), 36);
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
            t.fontStyle = FontStyles.Normal;

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
        void SetStatus(string s) => _statusText.text = s; // InfoBox ortak — sadece satır değişir

        /// <summary>Sırası gelen oyuncunun isim etiketi önce 2 kez yanıp söner, sonra
        /// dikkat çeken bir renkte sabit kalır — bir sonraki oyuncunun sırası gelene kadar.
        /// Geri alma (fast-forward) sırasında atlanır, gürültü yapmasın diye.</summary>
        void HighlightTurn(int seat)
        {
            if (_fast) return;
            if (_turnBlinkCo != null) StopCoroutine(_turnBlinkCo);
            if (_turnSeat >= 0 && _turnSeat != seat) TurnLabel(_turnSeat).color = _turnLabelBaseColor[_turnSeat];
            _turnSeat = seat;
            _turnBlinkCo = StartCoroutine(BlinkThenHighlight(seat));
        }

        System.Collections.IEnumerator BlinkThenHighlight(int seat)
        {
            var label = TurnLabel(seat);
            var normal = _turnLabelBaseColor[seat];
            for (int i = 0; i < 2; i++)
            {
                label.color = NameColorActive;
                yield return new WaitForSeconds(0.15f);
                label.color = normal;
                yield return new WaitForSeconds(0.15f);
            }
            label.color = NameColorActive;
        }

        /// <summary>Bir Text'in arkasına aynı hizada yarı saydam çip/arka plan koyar.</summary>
        void AddChip(TMP_Text label, Vector2 size, Color color)
        {
            var rt = (RectTransform)label.transform;
            var chip = new GameObject(label.name + "_Chip", typeof(Image));
            var crt = (RectTransform)chip.transform;
            crt.SetParent(rt.parent, false);
            crt.anchorMin = rt.anchorMin; crt.anchorMax = rt.anchorMax;
            crt.pivot = rt.pivot;
            crt.anchoredPosition = rt.anchoredPosition;
            crt.sizeDelta = size;
            var img = chip.GetComponent<Image>();
            img.sprite = _roundedSprite; img.type = Image.Type.Sliced; // yuvarlak köşe
            img.color = color;
            img.raycastTarget = false;
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

        /// <summary>Altın çerçeveli isim plakası (bien_zone deseni); içindeki Text döner.</summary>
        TMP_Text MakeNamePill(Vector2 anchor, Vector2 pos)
        {
            var go = new GameObject("NamePill", typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(_safe, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(290, 74);
            var im = go.GetComponent<Image>();
            im.sprite = _zoneSprite != null ? _zoneSprite : _roundedSprite;
            im.type = Image.Type.Sliced;
            im.color = _zoneSprite != null ? Color.white : new Color(0f, 0f, 0f, 0.45f);
            im.raycastTarget = false;
            var t = MakeText(rt, "Name", new Vector2(0.5f, 0.5f), Vector2.zero, 32, TextAnchor.MiddleCenter);
            ((RectTransform)t.transform).sizeDelta = new Vector2(290, 74);
            t.fontStyle = FontStyles.Normal;
            t.color = new Color(0.95f, 0.87f, 0.60f);
            return t;
        }

        /// <summary>Altın çerçeveli etiket (isim tahtası stili): bien_zone deseni + kalın altın yazı.
        /// Dönen Text'in PARENT'ı çerçevedir; SetBid/toggle çerçeveyi komple açar-kapar.</summary>
        TMP_Text MakeFramedLabel(Transform parent, Vector2 anchor, Vector2 pos, Vector2 size,
                             int font, float rotZ, bool startHidden)
        {
            var go = new GameObject("Framed", typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            if (rotZ != 0) rt.localRotation = Quaternion.Euler(0, 0, rotZ);
            var im = go.GetComponent<Image>();
            im.sprite = _plateSprite != null ? _plateSprite : _roundedSprite;
            im.type = Image.Type.Sliced;
            im.color = _plateSprite != null ? Color.white : new Color(0f, 0f, 0f, 0.45f);
            im.raycastTarget = false;
            var t = MakeText(rt, "L", new Vector2(0.5f, 0.5f), Vector2.zero, font, TextAnchor.MiddleCenter);
            ((RectTransform)t.transform).sizeDelta = size;
            if (_fancyFont != null) t.font = _fancyFont; // plaka stili
            t.fontStyle = FontStyles.Normal;
            t.color = new Color(1f, 0.9f, 0.45f);
            if (startHidden) go.SetActive(false);
            return t;
        }

        /// <summary>İhale çipi: kutu + yazı tek parça; SetBid boşken kutuyu tamamen gizler.</summary>
        TMP_Text MakeBidChip(Vector2 anchor, Vector2 pos)
        {
            var go = new GameObject("BidChip", typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(_safe, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(250, 48);
            var im = go.GetComponent<Image>();
            im.sprite = _roundedSprite; im.type = Image.Type.Sliced;
            im.color = new Color(0f, 0f, 0f, 0.45f);
            im.raycastTarget = false;
            var t = MakeText(rt, "L", new Vector2(0.5f, 0.5f), Vector2.zero, 28, TextAnchor.MiddleCenter);
            ((RectTransform)t.transform).sizeDelta = new Vector2(250, 48);
            t.color = new Color(1f, 0.9f, 0.45f);
            t.fontStyle = FontStyles.Normal;
            go.SetActive(false);
            return t;
        }

        /// <summary>Çerçeveli kutuyu sol sütun çizgisine sabitler: sol pivot + ortak çapa.
        /// Oransal çapa + sabit genişlikte sol kenarlar ekran oranına göre kayıyordu — bu sabitler.</summary>
        void PinLeft(TMP_Text label, float y)
        {
            var rt = (RectTransform)label.transform.parent;
            rt.anchorMin = rt.anchorMax = new Vector2(405f / 2532f, 1f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(0, y);
        }

        /// <summary>İhale çipini yazar; boş metin çipi gizler.</summary>
        void SetBid(int seat, string s)
        {
            _bidLabels[seat].text = s;
            _bidLabels[seat].transform.parent.gameObject.SetActive(!string.IsNullOrEmpty(s));
        }

        /// <summary>Dekor el alanı: altın konturlu 9-slice çip (bien_zone).</summary>
        void MakeZone(Transform parent, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var go = new GameObject("Zone", typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var im = go.GetComponent<Image>();
            im.sprite = _zoneSprite; im.type = Image.Type.Sliced;
            im.raycastTarget = false;
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
            AddShadow(img, 4f, -6f, 0.30f); // masada derinlik hissi
            return rt;
        }

        static TextAlignmentOptions ToTmp(TextAnchor a) => a switch
        {
            TextAnchor.UpperLeft => TextAlignmentOptions.TopLeft,
            TextAnchor.UpperCenter => TextAlignmentOptions.Top,
            TextAnchor.MiddleLeft => TextAlignmentOptions.Left,
            TextAnchor.MiddleRight => TextAlignmentOptions.Right,
            TextAnchor.LowerCenter => TextAlignmentOptions.Bottom,
            _ => TextAlignmentOptions.Center
        };

        TMP_Text MakeText(Transform parent, string name, Vector2 anchor, Vector2 pos, int size, TextAnchor align)
        {
            var go = new GameObject(name, typeof(TextMeshProUGUI));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(800, 60);
            var t = go.GetComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            t.fontSize = size;
            t.alignment = ToTmp(align);
            t.color = Color.white;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.raycastTarget = false;
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
            var img = go.GetComponent<Image>();
            img.sprite = _roundedSprite; img.type = Image.Type.Sliced; // yuvarlak köşe
            img.color = new Color(0.16f, 0.45f, 0.28f);
            AddShadow(img, 4f, -5f, 0.30f);
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img; // basma/üzerine gelme geri bildirimi çalışsın
            var cb = ColorBlock.defaultColorBlock;
            cb.highlightedColor = new Color(1.08f, 1.08f, 1.08f);
            cb.pressedColor = new Color(0.72f, 0.72f, 0.72f);
            cb.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.55f);
            btn.colors = cb;
            var t = MakeText(rt, "Label", new Vector2(0.5f, 0.5f), Vector2.zero, fontSize, TextAnchor.MiddleCenter);
            ((RectTransform)t.transform).sizeDelta = size;
            t.text = label;
            return btn;
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