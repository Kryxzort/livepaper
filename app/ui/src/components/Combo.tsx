import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { ChevronDown } from "lucide-react";

export interface ComboOpt { value: number; label: string }

// Editable number + preset dropdown (a combobox): type any value in the field, OR click the caret to
// pick a preset that fills the field in. Themed popover matching Select (native dropdowns can't be
// themed/translucent), portal'd to <body> so it escapes overflow + backdrop-filter containing blocks.
export function Combo({ value, options, onChange, min, max, step = 1, title }: {
  value: number; options: ComboOpt[]; onChange: (v: number) => void;
  min?: number; max?: number; step?: number; title?: string;
}) {
  const [open, setOpen] = useState(false);
  const [rect, setRect] = useState<{ left: number; top: number; width: number } | null>(null);
  const wrap = useRef<HTMLDivElement>(null);
  const pop = useRef<HTMLDivElement>(null);

  const place = () => { const r = wrap.current?.getBoundingClientRect(); if (r) setRect({ left: r.left, top: r.bottom + 4, width: r.width }); };
  useEffect(() => {
    if (!open) return;
    const close = () => setOpen(false);
    const onScroll = (e: Event) => { if (!pop.current?.contains(e.target as Node)) setOpen(false); };
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    addEventListener("resize", close);
    addEventListener("scroll", onScroll, true);
    addEventListener("keydown", onKey);
    return () => { removeEventListener("resize", close); removeEventListener("scroll", onScroll, true); removeEventListener("keydown", onKey); };
  }, [open]);

  return (
    <>
      <div ref={wrap} className="combo" title={title}>
        <input className="num combo-in" type="number" value={value} min={min} max={max} step={step}
          onChange={(e) => { const v = e.target.value; if (v !== "") onChange(+v); }} />
        <button type="button" className="combo-caret" title="Presets"
          onClick={(e) => { e.stopPropagation(); if (open) { setOpen(false); return; } place(); setOpen(true); }}>
          <ChevronDown size={14} />
        </button>
      </div>
      {open && rect && createPortal(
        <>
          <div className="sel-backdrop" onClick={() => setOpen(false)} onContextMenu={(e) => { e.preventDefault(); setOpen(false); }} />
          <div ref={pop} className="sel-pop" style={{ left: rect.left, top: rect.top, minWidth: rect.width }}>
            {options.map((o) => (
              <button key={o.value} type="button" className={`sel-opt${o.value === value ? " on" : ""}`}
                onClick={() => { onChange(o.value); setOpen(false); }}>{o.label}</button>
            ))}
          </div>
        </>, document.body)}
    </>
  );
}
