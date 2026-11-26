"""隠れマルコフモデル(HMM)で「今どの物体へ向かっているか」を時々刻々推定し、今のモデル(analyze_prediction.py の A〜D)と比べる(試作)。

モデル:
  隠れ状態 = 今の意図。 0 = 探索中(どれにも向かっていない) / k = 物体 k に向かっている(この先 --horizon 秒以内に k に到達する)
  観測     = step 秒ごと・到達しうる物体ごとの特徴量(analyze_arrival.py の realtime_samples と同じ)。
             HMM-A: 距離・近づく速さ / HMM-C: 距離・近づく速さ・体の向き・最終注視可能性(最後の 1 秒)
  出力分布 = 物体のあいだで共通。「向かっている物体の特徴量」と「それ以外の物体の特徴量」を、特徴量ごとの正規分布で表す
             (距離は log(距離 + 0.1) にしてから)。状態 k の尤度比 = Π_特徴量 N(x_k | 向かう) / N(x_k | それ以外)
  遷移     = 探索中が続く p00 / 向かうのが続く pkk / 向かう → 探索中 pk0 / 向かう → 別の物体 pkj。
             探索中から向かう先は、その時刻に到達しうる物体に等分。到達しうる物体でなくなった(到達した)状態は探索中に戻す
  推定     = 教師あり。各時刻の状態を「この先 horizon 秒以内に最初に到達する物体(無ければ探索中)」として、出力分布と遷移を数える
  予測     = forward アルゴリズム(その時刻までの観測だけ)で P(状態 = k | ここまで) を出し、それを「k に到達する確率」とする

評価(analyze_prediction.py と同じ): その人の時間を 5 つの連続した区間に分け、1 区間ずつ残りで学習して予測。
  到達の L 秒前に、到達した物体の確率が候補の中で1位だった割合(L = step〜horizon 秒)、AUC、AP。
書き出すもの(毎回上書き): <データの場所>Reports/HMM_<実験>_<実験対象者>_<人物>_<到達の決め方>/report.html, lead_accuracy.csv, metrics.csv

使い方:
  python Tools/analyze_hmm.py --data-root D:\\HomeExperiment --exports gomi/test_double2 --arrival touch
  python Tools/analyze_hmm.py --data-root assets --exports Oono_multiObject/sora --arrival destination --persons child
  --horizon 5 --step 0.5 … この先何秒以内の到達を「向かっている」とするか・何秒ごとか
  --tau 1             … 尤度比を何乗するか(特徴量どうしの相関で確率が極端になりすぎるときに 1 より小さくする)
numpy だけで動く。
"""

from __future__ import annotations

import argparse
import html
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from analyze_arrival import (  # noqa: E402
    BLOCKS, EXTRA_CONFIG, EXTRA_STYLE, blocks_of, choose_exports, list_exports, load_streams, realtime_samples)
from analyze_prediction import average_precision, oof_predictions, wilson, xy_chart  # noqa: E402
from toolbox import home_data_root  # noqa: E402
from toolbox.arrival_model import ARRIVAL_MODES, MODEL_LABELS, Config, PersonStream, arrival_definition, auc  # noqa: E402
from toolbox.report_html import SCRIPT, SERIES_DARK, SERIES_LIGHT, STYLE, num, pct, table_html, write_csv  # noqa: E402

HMM_FEATURES = {"HMM-A": ["dist", "approach"], "HMM-C": ["dist", "approach", "body", "final"]}
LABELS = {**MODEL_LABELS, "HMM-A": "HMM-A 空間のみ", "HMM-C": "HMM-C 空間 + 視線 + 体の向き"}
COLORS = {"A": "var(--muted)", "B": "var(--series-1)", "C": "var(--series-2)", "D": "var(--series-3)",
          "HMM-A": "var(--series-5)", "HMM-C": "var(--series-4)"}
FEATURE_NAMES = {"dist": "log(距離 + 0.1)", "approach": "直前2秒の距離の変化 (m)", "body": "肩の正面からの角度 (度)",
                 "final": "最終注視可能性"}


# ------------------------------------------------------------
# HMM
# ------------------------------------------------------------
def transform(name: str, v: float) -> float:
    return float(np.log(max(v, 0.0) + 0.1)) if name == "dist" and np.isfinite(v) else float(v)


def state_of(arrivals: list[tuple[str, float]], t: float, horizon: float, candidates: set[str]) -> str | None:
    """時刻 t の状態: この先 horizon 秒以内に最初に到達する物体(到達しうる物体のときだけ)。無ければ None = 探索中"""
    for name, at in arrivals:
        if t < at <= t + horizon:
            return name if name in candidates else None
    return None


class TiedHMM:
    """物体のあいだで出力分布を共通にした HMM。行(realtime_samples)を時刻ごとにまとめて使う"""

    def __init__(self, features: list[str], tau: float = 1.0):
        self.features, self.tau = features, tau

    def fit(self, rows: list[dict], states: dict[float, str | None], step: float) -> "TiedHMM":
        target = {f: [] for f in self.features}
        other = {f: [] for f in self.features}

        for r in rows:
            bucket = target if states.get(r["timeSec"]) == r["target"] else other
            for f in self.features:
                v = transform(f, r.get(f, np.nan))
                if np.isfinite(v):
                    bucket[f].append(v)

        def gaussian(values: list[float], fallback: tuple[float, float]) -> tuple[float, float]:
            if len(values) < 3:
                return fallback
            v = np.asarray(values)
            return float(v.mean()), float(max(v.std(), 1e-3 + 0.05 * fallback[1]))

        self.emission = {}
        for f in self.features:
            all_v = np.asarray(target[f] + other[f]) if target[f] + other[f] else np.array([0.0, 1.0])
            base = (float(all_v.mean()), float(max(all_v.std(), 1e-3)))
            self.emission[f] = {"target": gaussian(target[f], base), "other": gaussian(other[f], base)}

        # 遷移(連続した時刻の組だけ数える)
        times = sorted(states)
        n = {"00": 1.0, "0k": 1.0, "kk": 1.0, "k0": 1.0, "kj": 0.2}
        for a, b in zip(times, times[1:]):
            if b - a > 1.5 * step:
                continue
            s, u = states[a], states[b]
            if s is None:
                key = "00" if u is None else "0k"
            else:
                key = "k0" if u is None else ("kk" if u == s else "kj")
            n[key] += 1.0
        self.p00 = n["00"] / (n["00"] + n["0k"])
        stay = n["kk"] + n["k0"] + n["kj"]
        self.pkk, self.pk0, self.pkj = n["kk"] / stay, n["k0"] / stay, n["kj"] / stay
        return self

    def log_ratio(self, row: dict) -> float:
        total = 0.0
        for f in self.features:
            v = transform(f, row.get(f, np.nan))
            if not np.isfinite(v):
                continue
            (mt, st), (mo, so) = self.emission[f]["target"], self.emission[f]["other"]
            total += (-0.5 * ((v - mt) / st) ** 2 - np.log(st)) - (-0.5 * ((v - mo) / so) ** 2 - np.log(so))
        return float(np.clip(self.tau * total, -50, 50))

    def forward(self, by_time: dict[float, dict[str, int]], rows: list[dict], step: float) -> np.ndarray:
        """行ごとの P(状態 = その行の物体 | その時刻までの観測)"""
        out = np.full(len(rows), np.nan)
        alpha: dict[str | None, float] = {None: 1.0}
        last = None

        for t in sorted(by_time):
            cand = by_time[t]

            # 記録の途切れの後は、探索中から始め直す
            if last is not None and t - last > 3 * step:
                alpha = {None: 1.0}
            last = t

            names = list(cand)
            pred: dict[str | None, float] = {None: 0.0, **{k: 0.0 for k in names}}
            a0 = alpha.get(None, 0.0)
            pred[None] += a0 * self.p00
            for k in names:
                pred[k] += a0 * (1 - self.p00) / len(names)

            for s, a in alpha.items():
                if s is None or a == 0.0:
                    continue
                if s not in cand:   # 到達した・到達しうる物体でなくなった
                    pred[None] += a
                    continue
                pred[s] += a * self.pkk
                others = [k for k in names if k != s]
                pred[None] += a * (self.pk0 + (self.pkj if not others else 0.0))
                for k in others:
                    pred[k] += a * self.pkj / len(others)

            post = {None: pred[None]}
            for k in names:
                post[k] = pred[k] * np.exp(self.log_ratio(rows[cand[k]]))
            z = sum(post.values())
            alpha = {s: v / z for s, v in post.items()} if z > 0 else {None: 1.0}

            for k, i in cand.items():
                out[i] = alpha[k]

        return out


def group_rows(rows: list[dict]) -> dict[float, dict[str, int]]:
    by_time: dict[float, dict[str, int]] = {}
    for i, r in enumerate(rows):
        by_time.setdefault(r["timeSec"], {})[r["target"]] = i
    return by_time


def hmm_oof(stream: PersonStream, rows: list[dict], horizon: float, step: float, tau: float) -> tuple[dict[str, np.ndarray], dict]:
    """HMM の交差検証での予測(区間ごとに残りで学習し、forward は全体に通してその区間の値を取る)と、全体で学習したパラメータ"""
    by_time = group_rows(rows)
    states = {t: state_of(stream.arrivals, t, horizon, set(c)) for t, c in by_time.items()}
    block = blocks_of(rows)
    out, params = {}, {}

    for name, feats in HMM_FEATURES.items():
        prob = np.full(len(rows), np.nan)
        for b in range(BLOCKS):
            test = np.flatnonzero(block == b)
            train_rows = [rows[i] for i in np.flatnonzero(block != b)]
            train_times = {r["timeSec"] for r in train_rows}
            train_states = {t: s for t, s in states.items() if t in train_times}
            if not test.size or not any(s is not None for s in train_states.values()):
                continue
            model = TiedHMM(feats, tau).fit(train_rows, train_states, step)
            prob[test] = model.forward(by_time, rows, step)[test]
        out[name] = prob
        params[name] = TiedHMM(feats, tau).fit(rows, states, step)

    return out, params


# ------------------------------------------------------------
# 評価
# ------------------------------------------------------------
def lead_accuracy(stream: PersonStream, rows: list[dict], probs: dict[str, np.ndarray], leads: np.ndarray, step: float) -> dict:
    """{モデル: {L: [(到達, 当たったか, 候補の数)]}}(analyze_prediction.lead_table と同じ数え方)"""
    by_time = group_rows(rows)
    times = np.array(sorted(by_time))
    result = {m: {float(L): [] for L in leads} for m in probs}

    for index, (arrived, t) in enumerate(stream.arrivals, 1):
        for L in leads:
            k = np.searchsorted(times, t - L + 1e-6) - 1
            if k < 0 or t - L - times[k] > step:
                continue
            cand = by_time[times[k]]
            if arrived not in cand or len(cand) < 2:
                continue
            for m, prob in probs.items():
                p = {n: prob[i] for n, i in cand.items()}
                if all(np.isfinite(v) for v in p.values()):
                    result[m][float(L)].append((index, max(p, key=p.get) == arrived, len(p)))

    return result


def main() -> None:
    defaults = Config()
    parser = argparse.ArgumentParser(description="HMM で到達先を時々刻々推定し、今のモデルと比べる")
    parser.add_argument("--exports", nargs="*", help="使う書き出し(実験/実験対象者[/骨格])")
    parser.add_argument("--persons", nargs="*", help="この名前の人物だけ")
    parser.add_argument("--horizon", type=float, default=5.0)
    parser.add_argument("--tau", type=float, default=1.0, help="尤度比を何乗するか")
    parser.add_argument("--min-arrivals", type=int, default=5, help="到達がこれより少ない人物は比べない")
    for name in ("sigma", "window", "high", "near", "leave", "step", "smooth"):
        parser.add_argument(f"--{name}", type=float, default=getattr(defaults, name))
    parser.add_argument("--arrival", choices=ARRIVAL_MODES, default=defaults.arrival)
    for name in ("dwell", "face", "look", "touch_margin", "touch_min", "touch_gap"):
        parser.add_argument(f"--{name.replace('_', '-')}", dest=name, type=float, default=getattr(defaults, name))
    parser.add_argument("--unity-labels", dest="unity_labels", action="store_true")
    home_data_root.add_argument(parser)
    args = parser.parse_args()

    root = home_data_root.choose_data_root(args.data_root)
    if root is None:
        return
    found = list_exports(root)
    exports = ([f for f in found if any("/".join(f).startswith(x.strip("/")) for x in args.exports)]
               if args.exports else choose_exports(found))
    if not exports:
        print("書き出しが選ばれていません。")
        return

    cfg = Config(horizon=args.horizon, **{k: getattr(args, k) for k in ("sigma", "window", "high", "near", "leave", "step", "smooth", *EXTRA_CONFIG)})
    print("\n読み込み:")
    streams = [s for s in load_streams(root, exports, cfg, args.persons) if len(s.arrivals) >= args.min_arrivals]
    if not streams:
        print(f"到達が {args.min_arrivals} 回以上ある人物がいません。")
        return

    leads = np.round(np.arange(cfg.step, cfg.horizon + 1e-6, cfg.step), 3)
    order = ["A", "C", "HMM-A", "HMM-C", "B", "D"]

    for stream in streams:
        print(f"\n{stream.key}: 到達 {len(stream.arrivals)} 回 …")
        rows = realtime_samples(stream)
        labels = np.array([r["label"] for r in rows])
        probs = oof_predictions(rows)
        hmm_probs, params = hmm_oof(stream, rows, cfg.horizon, cfg.step, args.tau)
        probs.update(hmm_probs)
        table = lead_accuracy(stream, rows, probs, leads, cfg.step)

        metrics, lead_rows = [], []
        for m in order:
            ok = np.isfinite(probs[m])
            hits = {L: table[m][float(L)] for L in leads}
            for L in leads:
                k, n = sum(h for _, h, _ in hits[L]), len(hits[L])
                lead_rows.append({"model": m, "leadSec": float(L), "events": n, "top1": k / n if n else np.nan,
                                  "chance": float(np.mean([1 / c for *_, c in hits[L]])) if n else np.nan})
            k1, n1 = sum(h for _, h, _ in hits[leads[1]]), len(hits[leads[1]])
            k3 = [r for r in lead_rows if r["model"] == m and abs(r["leadSec"] - 3.0) < 1e-6]
            metrics.append({"model": m, "auc": auc(probs[m][ok], labels[ok]), "ap": average_precision(probs[m], labels),
                            "top1At1s": k1 / n1 if n1 else np.nan, "top1At3s": k3[0]["top1"] if k3 else np.nan,
                            "meanTop1": float(np.nanmean([r["top1"] for r in lead_rows if r["model"] == m]))})
            print(f"  {m:6s} AUC {metrics[-1]['auc']:.2f}  AP {metrics[-1]['ap']:.3f}  1秒前 {pct(metrics[-1]['top1At1s'])}"
                  f"  3秒前 {pct(metrics[-1]['top1At3s'])}  平均 {pct(metrics[-1]['meanTop1'])}")

        out_dir = home_data_root.report_root(root) / f"HMM_{stream.experiment}_{stream.key.replace('/', '_')}_{cfg.arrival}"
        out_dir.mkdir(exist_ok=True)
        write_csv(out_dir / "lead_accuracy.csv", lead_rows)
        write_csv(out_dir / "metrics.csv", metrics)
        write_report(out_dir / "report.html", stream, cfg, args.tau, leads, lead_rows, metrics, params, labels)
        print(f"  → {out_dir / 'report.html'}")


def write_report(path: Path, stream: PersonStream, cfg: Config, tau: float, leads: np.ndarray, lead_rows: list[dict],
                 metrics: list[dict], params: dict, labels: np.ndarray) -> None:
    order = [m["model"] for m in metrics]
    series = [{"name": LABELS[m], "label": m, "color": COLORS[m], "dashed": not m.startswith("HMM"), "emphasized": m.startswith("HMM"),
               "values": np.array([r["top1"] for r in lead_rows if r["model"] == m])} for m in order]
    chance = np.array([r["chance"] for r in lead_rows if r["model"] == "A"])
    series.append({"name": "当て推量", "label": "当て推量", "color": "var(--muted)", "dashed": True, "values": chance})

    sec = [f"<h2>1 何秒前から当たるか</h2><p class='note'>到達の L 秒前の予測で、到達した物体の確率が候補の中で1位だった割合。"
           f"実線 = HMM、破線 = 今のモデル(ロジスティック回帰)。学習はこの人の他の時間(5 分割)だけ。</p>",
           "<div class='card'>" + xy_chart(leads, series, "何秒前に到達先が1位だったか", "到達の何秒前 (秒)", "1位だった割合", (0, 1),
                                           width=640, x_fmt=lambda v: f"{v:g}") + "</div>"]
    header = ["モデル"] + [f"{L:g} 秒前" for L in leads[1::2]]
    sec.append(table_html(header, [[html.escape(LABELS[m])] + [pct(r["top1"]) for r in lead_rows if r["model"] == m and r["leadSec"] in leads[1::2]]
                                   for m in order] + [["当て推量"] + [pct(r["chance"]) for r in lead_rows if r["model"] == "A" and r["leadSec"] in leads[1::2]]]))
    sec.append("<h2>2 確率としての良さ</h2><p class='note'>AUC = 「この先 horizon 秒以内に到達する物体・時刻」と「しない物体・時刻」を1つずつ選んだとき、"
               f"到達する方の確率が高い割合。AP = 確率の高い順に警告を出したときの当たりやすさ(陽性の割合 {pct(labels.mean())} と比べる)。</p>")
    sec.append(table_html(["モデル", "AUC", "AP", "1 秒前に1位", "3 秒前に1位", "全部の L の平均"],
                          [[html.escape(LABELS[m["model"]]), num(m["auc"], 2), num(m["ap"], 3), pct(m["top1At1s"]), pct(m["top1At3s"]), pct(m["meanTop1"])]
                           for m in metrics]))

    sec.append("<h2>3 学習した HMM(全部の時間で学習)</h2>")
    for name, model in params.items():
        sec.append(f"<h3>{html.escape(LABELS[name])}</h3><p class='note'>遷移(step {cfg.step:g} 秒あたり): 探索中が続く {model.p00:.3f} / "
                   f"向かうのが続く {model.pkk:.3f} / 向かう → 探索中 {model.pk0:.3f} / 向かう → 別の物体 {model.pkj:.3f}"
                   f"(向かう状態の平均の長さ ≈ {cfg.step / max(1 - model.pkk, 1e-6):.1f} 秒)</p>")
        sec.append(table_html(["特徴量", "向かう物体: 平均", "標準偏差", "それ以外: 平均", "標準偏差"],
                              [[html.escape(FEATURE_NAMES[f]), num(e["target"][0], 2), num(e["target"][1], 2), num(e["other"][0], 2), num(e["other"][1], 2)]
                               for f, e in model.emission.items()]))

    sec.append("<h2>方法</h2><ul class='note'>"
               f"<li>{html.escape(arrival_definition(cfg))}。</li>"
               f"<li>隠れ状態 = 探索中 / 物体 k に向かっている(この先 {cfg.horizon:g} 秒以内に k に最初に到達する)。教師あり(各時刻の状態をこの定義で付けて数える)。</li>"
               "<li>観測 = analyze_prediction.py と同じ時刻・候補ごとの特徴量。HMM-A は距離・近づく速さ、HMM-C はそれに体の向きと最終注視可能性を足す。"
               "出力分布は物体のあいだで共通の正規分布(向かう物体 / それ以外)。距離は log(距離 + 0.1)。欠けている特徴量は使わない。</li>"
               f"<li>尤度比を {tau:g} 乗。特徴量は独立とみなしている(相関があると確率が極端になりやすいが、1位の順位にはあまり影響しない)。</li>"
               "<li>予測 = forward アルゴリズム(その時刻までの観測だけ)。探索中から向かう先は到達しうる物体に等分。到達した物体の状態は探索中に戻す。"
               "記録が途切れたら探索中から始め直す。</li>"
               "<li>A〜D は analyze_prediction.py と同じロジスティック回帰(過去 10 秒の特徴量を時刻ごとに独立に使う)。</li></ul>")

    light_vars = " ".join(f"--series-{i + 1}: {c};" for i, c in enumerate(SERIES_LIGHT))
    dark_vars = " ".join(f"--series-{i + 1}: {c};" for i, c in enumerate(SERIES_DARK))
    page = (f"<!doctype html><html lang='ja'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>"
            f"<title>HMM の比較</title><style>{STYLE.replace('%LIGHT%', light_vars).replace('%DARK%', dark_vars)}{EXTRA_STYLE}</style>"
            f"<script>{SCRIPT}</script></head><body><main>"
            f"<h1>{html.escape(stream.key)}: HMM と今のモデルの比較(試作)</h1>"
            f"<p class='meta'>{html.escape(stream.experiment)} / {stream.time[-1] - stream.time[0]:.0f} 秒 / 到達 {len(stream.arrivals)} 回 / "
            f"この先 {cfg.horizon:g} 秒以内の到達 / {cfg.step:g} 秒ごと</p>" + "".join(sec) + "</main></body></html>")
    path.write_text(page, encoding="utf-8")


if __name__ == "__main__":
    main()
