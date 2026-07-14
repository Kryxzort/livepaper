import { useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import {
  DndContext, DragOverlay, closestCenter, PointerSensor, useSensor, useSensors,
  type DragEndEvent, type DragStartEvent,
} from "@dnd-kit/core";
import { SortableContext, horizontalListSortingStrategy, useSortable } from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import {
  ChevronRight, ChevronDown, SlidersHorizontal, Save, FolderOpen, Play, X,
} from "lucide-react";
import { useStore } from "../store";
import { type LibraryItem } from "../api/client";
import { SavePlaylistModal } from "./SavePlaylistModal";
import { LoadPlaylistModal } from "./LoadPlaylistModal";
import { PlaylistSettingsModal } from "./PlaylistSettingsModal";
import { MotionThumb, hasGifAsset } from "./MotionThumb";
import { useSmoothScroll } from "../hooks/useSmoothScroll";

function PlItem({ path, item, index, playing }: { path: string; item?: LibraryItem; index: number; playing: boolean }) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: path });
  const removeFromPlaylist = useStore((s) => s.removeFromPlaylist);
  const playFrom = useStore((s) => s.playFrom);
  const openExternal = useStore((s) => s.openExternal);
  const openItemSettings = useStore((s) => s.openItemSettings);
  // gate autoplay on window focus/visibility (see App.tsx appActive) → backgrounded = gifs unmount, no decode drain
  const autoPlayGifs = useStore((s) => !!(s.settings?.autoPlayGifs) && s.appActive);
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null);
  const [hover, setHover] = useState(false);
  // COOL: the hover glow can't escape the x-scrolling row (overflow clips it vertically). Render it as
  // a position:fixed layer portal'd to <body> at the item's rect → it floats OVER the strip chrome with
  // zero row padding. rAF-throttled rect tracking so it follows the item while the row scrolls.
  const [glow, setGlow] = useState<{ x: number; y: number; w: number; h: number } | null>(null);
  const rafRef = useRef(0);
  // getBoundingClientRect already reflects the hover lift transform — don't offset, or the glow drifts up.
  const place = (el: HTMLElement) => { const r = el.getBoundingClientRect(); setGlow({ x: r.left, y: r.top, w: r.width, h: r.height }); };
  const onEnter = (e: React.MouseEvent<HTMLDivElement>) => { setHover(true); place(e.currentTarget); };
  const onMove = (e: React.MouseEvent<HTMLDivElement>) => { const el = e.currentTarget; if (rafRef.current) return; rafRef.current = requestAnimationFrame(() => { rafRef.current = 0; place(el); }); };
  const onLeave = () => { setHover(false); setGlow(null); if (rafRef.current) { cancelAnimationFrame(rafRef.current); rafRef.current = 0; } };
  // morph source for THIS strip item's settings modal (namespaced pl- so it doesn't collide with the
  // library card's lib- id for the same wallpaper).
  const settingsOpen = useStore((s) => s.settingsItem?.videoPath === path && s.settingsMorphId === `pl-${path}`);
  const active = !!item && !settingsOpen && (hover || (autoPlayGifs && hasGifAsset(item)));
  // while dragging, the visible copy is the DragOverlay (portal'd above all UI); hide the in-place
  // source but keep it mounted so the sortable still reserves its gap.
  const style = { transform: CSS.Transform.toString(transform), transition, opacity: isDragging ? 0 : 1 };
  return (
    <div ref={setNodeRef} style={style} className={`pl-item${playing ? " playing" : ""}`}
      {...attributes} {...listeners} onClick={() => playFrom(index)} title={item?.title}
      onMouseEnter={onEnter} onMouseMove={onMove} onMouseLeave={onLeave}
      onContextMenu={(e) => { e.preventDefault(); setMenu({ x: e.clientX, y: e.clientY }); }}>
      {item && <MotionThumb item={item} active={active} morphId={settingsOpen ? `pl-${path}` : undefined} />}
      {/* #99000000 title bar, 10px white ellipsis */}
      <div className="pl-title">{item?.title ?? ""}</div>
      <button className="pl-remove" title="Remove from playlist"
        onClick={(e) => { e.stopPropagation(); removeFromPlaylist(path); }}><X size={21} /></button>
      {glow && !playing && !isDragging && createPortal(
        <div className="pl-hover-glow" style={{ left: glow.x, top: glow.y, width: glow.w, height: glow.h }} />, document.body)}
      {menu && item && createPortal(
        <>
          <div className="ctx-backdrop" onClick={() => setMenu(null)} onContextMenu={(e) => { e.preventDefault(); setMenu(null); }} />
          {/* portal'd: the strip's backdrop-filter would otherwise trap position:fixed */}
          <div className="ctx-menu pl-ctx" style={{ left: menu.x, top: menu.y }} onClick={() => setMenu(null)}>
            <button onClick={() => removeFromPlaylist(path)}>Remove from playlist</button>
            <button onClick={() => openItemSettings(item, [item], `pl-${path}`)}>Settings (volume / speed)</button>
            <button onClick={() => openExternal({ path: item.videoPath, reveal: true, isScene: item.isScene, workshopId: item.workshopId, copiedSceneDir: item.copiedSceneDir })}>Open in file manager</button>
          </div>
        </>, document.body)}
    </div>
  );
}

// The thing shown floating under the cursor while dragging — rendered in a DragOverlay portal'd to
// <body>, so it sits ABOVE all UI (not clipped by the strip's overflow / backdrop-filter containing block).
function PlItemOverlay({ item }: { item?: LibraryItem }) {
  return (
    <div className="pl-item dragging" style={{ cursor: "grabbing", boxShadow: "0 12px 28px rgba(0,0,0,.5)", transform: "scale(1.05)" }}>
      {item && <MotionThumb item={item} active={false} />}
      <div className="pl-title">{item?.title ?? ""}</div>
    </div>
  );
}

export function PlaylistStrip() {
  const playlist = useStore((s) => s.playlist);
  const items = useStore((s) => s.libraryItems);
  const playingPath = useStore((s) => s.playingPath);
  const reorder = useStore((s) => s.reorderPlaylist);
  const play = useStore((s) => s.playPlaylist);
  const ps = useStore((s) => s.playlistSettings);
  const setPS = useStore((s) => s.setPlaylistSettings);
  const names = useStore((s) => s.playlistNames);
  const saveAs = useStore((s) => s.savePlaylistAs);
  const loadNamed = useStore((s) => s.loadNamedPlaylist);
  const collapsed = useStore((s) => !!(s.settings?.isPlaylistCollapsed));
  const autoAdd = useStore((s) => !!(s.settings?.autoAddLibraryToPlaylist));
  const setSetting = useStore((s) => s.setSetting);

  const [showSettings, setShowSettings] = useState(false);
  const [showSave, setShowSave] = useState(false);
  const [showLoad, setShowLoad] = useState(false);
  const [dragId, setDragId] = useState<string | null>(null); // the item currently picked up (→ DragOverlay)
  const byPath = useMemo(() => Object.fromEntries(items.map((i) => [i.videoPath, i])), [items]);
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 6 } }));
  const rowRef = useRef<HTMLDivElement>(null);
  useSmoothScroll(rowRef, "x");
  // expose the strip's live height as --strip-h so the library scroll can pad its bottom enough to
  // reveal the last row above the bar — whether it's collapsed or expanded.
  const stripRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = stripRef.current;
    if (!el) return;
    const apply = () => document.documentElement.style.setProperty("--strip-h", `${Math.round(el.offsetHeight)}px`);
    apply();
    const ro = new ResizeObserver(apply);
    ro.observe(el);
    return () => { ro.disconnect(); document.documentElement.style.removeProperty("--strip-h"); };
  }, []);

  const onDragStart = (e: DragStartEvent) => setDragId(String(e.active.id));
  const onDragEnd = (e: DragEndEvent) => {
    setDragId(null);
    // FIX: after a pointer drop dnd-kit leaves focus on the (now-reordered) item → a stray :focus-visible
    // outline lands on the wrong card until the mouse moves. Blur it so the outline clears immediately.
    (document.activeElement as HTMLElement | null)?.blur?.();
    const { active, over } = e;
    if (over && active.id !== over.id) {
      const from = playlist.indexOf(String(active.id)), to = playlist.indexOf(String(over.id));
      if (from >= 0 && to >= 0) reorder(from, to);
    }
  };
  const onDragCancel = () => { setDragId(null); (document.activeElement as HTMLElement | null)?.blur?.(); };

  return (
    <div className="strip" ref={stripRef}>
      <div className="strip-head">
        <button className="mini" title={collapsed ? "Expand" : "Collapse"}
          onClick={() => setSetting("isPlaylistCollapsed", !collapsed)}>{collapsed ? <ChevronRight size={16} /> : <ChevronDown size={16} />}</button>
        <span className="strip-label">PLAYLIST</span>
        <span className="strip-count">{playlist.length}</span>
        <button className="mini ico" title="Playlist settings" onClick={() => setShowSettings(true)}><SlidersHorizontal size={15} /></button>
        <button className="mini ico" title="Save playlist" onClick={() => setShowSave(true)}><Save size={15} /></button>
        <button className="mini ico" title="Load playlist" onClick={() => setShowLoad(true)}><FolderOpen size={15} /></button>
        <div className="spacer" />
        <button className="play-btn ico" onClick={() => play()} disabled={playlist.length === 0}><Play size={15} /> Play</button>
      </div>

      {!collapsed && (playlist.length === 0 ? (
        <div className="strip-empty">Add wallpapers with the toggle on library cards</div>
      ) : (
        <DndContext sensors={sensors} collisionDetection={closestCenter}
          onDragStart={onDragStart} onDragEnd={onDragEnd} onDragCancel={onDragCancel}>
          <SortableContext items={playlist} strategy={horizontalListSortingStrategy}>
            <div className="strip-row" ref={rowRef}>
              {playlist.map((p, i) => <PlItem key={p} path={p} item={byPath[p]} index={i} playing={p === playingPath} />)}
            </div>
          </SortableContext>
          {/* DragOverlay portal'd to <body> → the picked-up item floats ABOVE all UI (the strip's
              overflow + backdrop-filter would otherwise clip an in-place transform) */}
          {createPortal(<DragOverlay>{dragId ? <PlItemOverlay item={byPath[dragId]} /> : null}</DragOverlay>, document.body)}
        </DndContext>
      ))}

      {/* portal'd to body so the strip's backdrop-filter containing block doesn't pin them off-screen */}
      {createPortal(<>
        <PlaylistSettingsModal open={showSettings} ps={ps} setPS={setPS}
          autoAdd={autoAdd} setAutoAdd={(v) => setSetting("autoAddLibraryToPlaylist", v)} onClose={() => setShowSettings(false)} />
        <SavePlaylistModal open={showSave} names={names} onSave={saveAs} onClose={() => setShowSave(false)} />
        <LoadPlaylistModal open={showLoad} names={names} onLoad={loadNamed} onClose={() => setShowLoad(false)} />
      </>, document.body)}
    </div>
  );
}
