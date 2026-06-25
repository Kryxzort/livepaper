import { useEffect, useMemo, useRef, useState } from "react";
import { AnimatePresence, motion } from "framer-motion";
import { Search, CheckCheck, Star, RefreshCw } from "lucide-react";
import { api, media, type TransitionEffect, type LibraryItem } from "../api/client";
import { useStore } from "../store";
import { preview, type Source } from "./transitionPreview";

// The transition config slice this picker edits (lives on PlaylistSettings or the global AppSettings).
export interface TransitionValue {
  transitionEnabled: boolean;
  transitionEffectIds: string[];
  transitionDurationMs: number;
  transitionDurationMaxMs: number;
  transitionShuffle: boolean;
}

let _catalog: TransitionEffect[] | null = null; // module cache (fetch once per session)
const VIDEO_RE = /\.(mp4|webm|mkv|mov|m4v)$/i;

export function TransitionPicker({
  open, value, onChange, onClose, title = "Transitions",
}: {
  open: boolean; value: TransitionValue;
  onChange: (p: Partial<TransitionValue>) => void; onClose: () => void; title?: string;
}) {
  const [effects, setEffects] = useState<TransitionEffect[]>(_catalog ?? []);
  const [q, setQ] = useState("");
  const [hovered, setHovered] = useState<string | null>(null);
  const [ready, setReady] = useState(false);
  const libraryItems = useStore((s) => s.libraryItems);
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    if (!open || _catalog) return;
    api.transitions().then((e) => { _catalog = e; setEffects(e); }).catch(() => {});
  }, [open]);

  // shuffle is always-on now (the toggle was removed) — keep the persisted flag true.
  useEffect(() => { if (open && value.transitionShuffle !== true) onChange({ transitionShuffle: true }); }, [open, value.transitionShuffle]); // eslint-disable-line

  // pick a random pair of the user's wallpapers (video + a thumbnail poster) for the demo.
  const pickSources = (): [Source, Source] => {
    const poster = (i: LibraryItem, idx: number) =>
      i.thumbnailPath ? media(i.thumbnailPath) : api.transitionPreviewUrl(idx ? "b" : "a");
    const vids = libraryItems.filter((i) => !i.isScene && VIDEO_RE.test(i.videoPath));
    const pick = [...vids].sort(() => Math.random() - 0.5).slice(0, 2);
    if (pick.length === 2)
      return [{ video: media(pick[0].videoPath), poster: poster(pick[0], 0) },
              { video: media(pick[1].videoPath), poster: poster(pick[1], 1) }];
    return [{ poster: api.transitionPreviewUrl("a") }, { poster: api.transitionPreviewUrl("b") }];
  };
  const refreshSources = () => { const [x, y] = pickSources(); preview.setSources(x, y); };

  // bind the shared preview engine while the picker is open; retry the GL attach in the background
  // until WebGL is up, then the live shader preview renders.
  useEffect(() => {
    if (!open || !canvasRef.current) return;
    const [x, y] = pickSources();
    let timer: ReturnType<typeof setTimeout>;
    const tryAttach = () => {
      if (!canvasRef.current) return;
      if (preview.attach(canvasRef.current)) {
        preview.setSources(x, y);   // start the GL preview + video streams only once WebGL is up
        preview.setDuration(value.transitionDurationMs);
        preview.start(); setReady(true);
      } else {
        setReady(false);            // WebGL not up yet → retry quickly (Electron 42 has it ready instantly)
        timer = setTimeout(tryAttach, 250);
      }
    };
    tryAttach();
    return () => { clearTimeout(timer); setReady(false); preview.detach(); };
  }, [open]); // eslint-disable-line

  const sel = new Set(value.transitionEffectIds);
  const previewId = hovered ?? value.transitionEffectIds.find((id) => effects.some((e) => e.id === id)) ?? effects[0]?.id ?? null;
  const previewName = effects.find((e) => e.id === previewId)?.name;

  // drive the preview's effect + duration
  useEffect(() => {
    if (!ready || !previewId) return;
    const e = effects.find((x) => x.id === previewId);
    preview.setEffect(previewId, e?.uniforms ?? []);
  }, [previewId, ready, effects]);
  useEffect(() => { if (ready) preview.setDuration(value.transitionDurationMs); }, [value.transitionDurationMs, ready]);

  const toggle = (id: string) => {
    const next = new Set(sel); next.has(id) ? next.delete(id) : next.add(id);
    onChange({ transitionEffectIds: [...next] });
  };
  const setIds = (ids: string[]) => onChange({ transitionEffectIds: ids });

  // group filtered effects into category sections (sorted)
  const sections = useMemo(() => {
    const f = effects.filter((e) => q === "" || e.name.toLowerCase().includes(q.toLowerCase()) || e.id.toLowerCase().includes(q.toLowerCase()));
    const byCat = new Map<string, TransitionEffect[]>();
    for (const e of f) (byCat.get(e.category) ?? byCat.set(e.category, []).get(e.category)!).push(e);
    return [...byCat.entries()].sort((a, b) => a[0].localeCompare(b[0]));
  }, [effects, q]);

  const en = value.transitionEnabled;
  const rangeOn = value.transitionDurationMaxMs > value.transitionDurationMs;

  return (
    <AnimatePresence>
      {open && (
        <motion.div className="modal-backdrop" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} onClick={onClose}>
          <motion.div className="modal tr-modal" initial={{ scale: 0.96, opacity: 0 }} animate={{ scale: 1, opacity: 1 }}
            exit={{ scale: 0.96, opacity: 0 }} transition={{ type: "spring", stiffness: 320, damping: 28 }}
            onClick={(e) => e.stopPropagation()}>
            <div className="modal-body tr-body">
              <div className="tr-head">
                <h2>{title}</h2>
                <div className="tr-head-right">
                  <label className="ck big"><input type="checkbox" checked={en}
                    onChange={(e) => onChange({ transitionEnabled: e.target.checked })} /> Enable transitions</label>
                  <button className="btn accent tr-done" onClick={onClose}>Done</button>
                </div>
              </div>

              <div className={en ? "" : "dim"}>
                {/* 16:9 live preview slot */}
                <div className="tr-preview">
                  <canvas ref={canvasRef} width={1280} height={720} className="tr-prev-canvas" />
                  <span className="tr-prev-cap">{previewName ?? "hover an effect to preview"}</span>
                  <button className="tr-refresh mini" title="Try different wallpapers" disabled={!en}
                    onClick={refreshSources}><RefreshCw size={15} /></button>
                </div>

                <div className="tr-row">
                  <span className="field-label">DURATION</span>
                  <input className="num" type="number" min={50} max={10000} step={50} disabled={!en}
                    value={value.transitionDurationMs}
                    onChange={(e) => onChange({ transitionDurationMs: Math.max(50, +e.target.value || 0) })} /><span>ms</span>
                  <button type="button" className={`tr-toggle${rangeOn ? " on" : ""}`} disabled={!en}
                    onClick={() => onChange({ transitionDurationMaxMs: rangeOn ? 0 : value.transitionDurationMs + 400 })}>
                    Random range</button>
                  {rangeOn && (<>
                    <span>to</span>
                    <input className="num" type="number" min={value.transitionDurationMs} max={10000} step={50} disabled={!en}
                      value={value.transitionDurationMaxMs}
                      onChange={(e) => onChange({ transitionDurationMaxMs: +e.target.value || 0 })} /><span>ms</span>
                  </>)}
                </div>

                <div className="tr-row">
                  <div className="tr-search"><Search size={14} />
                    <input placeholder="Search effects…" value={q} disabled={!en} onChange={(e) => setQ(e.target.value)} /></div>
                  <span className="tr-count">{sel.size} selected</span>
                  <button className="btn ghost ico" disabled={!en}
                    onClick={() => setIds(sel.size >= effects.length ? [] : effects.map((e) => e.id))}>
                    <CheckCheck size={14} />Toggle all</button>
                  <button className="btn ghost ico" disabled={!en} onClick={() => setIds(effects.filter((e) => e.defaultOn).map((e) => e.id))}><Star size={14} />Default</button>
                </div>

                {/* sections: category label + text-label chips */}
                <div className="tr-list scroll">
                  {sections.map(([cat, list]) => (
                    <div key={cat} className="tr-sec">
                      <div className="tr-sec-label">{cat}</div>
                      <div className="tr-chips">
                        {list.map((e) => (
                          <button key={e.id} className={`tr-chip${sel.has(e.id) ? " on" : ""}${previewId === e.id ? " hov" : ""}`}
                            disabled={!en} onMouseEnter={() => setHovered(e.id)} onClick={() => toggle(e.id)}>{e.name}</button>
                        ))}
                      </div>
                    </div>
                  ))}
                  {sections.length === 0 && <div className="tr-empty">No effects match.</div>}
                </div>
              </div>
            </div>
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>
  );
}
