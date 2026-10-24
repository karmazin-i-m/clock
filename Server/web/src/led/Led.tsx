import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { marqueeStrip, renderFrame, type Column } from './render';
import { ROWS } from './font';

export type LedMode = 'normal' | 'night' | 'off';

type Props = {
  /** One screen. Ignored when `frames` has more than one entry. */
  text?: string;
  /** Several screens: they slide through the panel like the clock's own marquee. */
  frames?: readonly string[];
  pixel?: number;
  cols?: number;
  mode?: LedMode;
  /** Blink every ':' once a second, as the clock screen does. */
  blink?: boolean;
  /** No plate, unlit pixels invisible — the logo. */
  bare?: boolean;
  label?: string;
};

// The marquee's pace, from design/Led.dc.html: a column every 55 ms, and ~1.9 s of rest each
// time a whole screen sits in the window.
const STEP_MS = 55;
const HOLD_STEPS = 34;
const BLINK_MS = 500;

const gapFor = (p: number) => Math.max(1, Math.round(p * 0.28));
const padFor = (p: number) => Math.round(p * 1.2);
const widthFor = (p: number, cols: number, bare: boolean) => cols * p + (cols - 1) * gapFor(p) + (bare ? 0 : 2 * padFor(p));

/**
 * The largest pixel up to `pixel` whose panel fits the space it is given, so a 24-column
 * panel drawn at 12 px on a desktop card still fits a 360 px phone instead of being cut off.
 */
function useFittedPixel(pixel: number, cols: number, bare: boolean) {
  const ref = useRef<HTMLDivElement>(null);
  const [available, setAvailable] = useState<number | null>(null);

  useLayoutEffect(() => {
    const element = ref.current;
    if (!element || typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(([entry]) => setAvailable(entry!.contentRect.width));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  let p = pixel;
  while (available !== null && p > 2 && widthFor(p, cols, bare) > available) p--;
  return [ref, p] as const;
}

function usePrefersReducedMotion() {
  const query = '(prefers-reduced-motion: reduce)';
  const [reduced, setReduced] = useState(() => typeof matchMedia === 'function' && matchMedia(query).matches);
  useEffect(() => {
    const list = matchMedia(query);
    const update = () => setReduced(list.matches);
    list.addEventListener('change', update);
    return () => list.removeEventListener('change', update);
  }, []);
  return reduced;
}

/**
 * The 24×8 panel (HANDOFF §4.1). Round pixels with a halo of the accent; `night` keeps the
 * colour at 45 % with a weaker halo, `off` is the grey of a clock that is not reporting.
 * With reduced motion the marquee steps from screen to screen instead of sliding.
 */
export function Led({ text = '', frames, pixel = 10, cols = 24, mode = 'normal', blink = false, bare = false, label }: Props) {
  const reduced = usePrefersReducedMotion();
  const [fitRef, p] = useFittedPixel(pixel, cols, bare);
  const sliding = frames !== undefined && frames.length > 1;
  const [colonOn, setColonOn] = useState(true);
  const [offset, setOffset] = useState(0);

  useEffect(() => {
    if (!blink || sliding) return;
    const timer = setInterval(() => setColonOn((on) => !on), BLINK_MS);
    return () => clearInterval(timer);
  }, [blink, sliding]);

  // Keyed on the text, not the array: a parent re-rendering with an equal list must not send
  // the marquee back to its first screen.
  const framesKey = sliding ? frames.join('|') : '';
  const strip = useMemo(() => (framesKey ? marqueeStrip(framesKey.split('|'), cols) : null), [framesKey, cols]);

  useEffect(() => {
    setOffset(0);
    if (!strip) return;
    const total = strip.length - cols;
    if (reduced) {
      const timer = setInterval(() => setOffset((o) => (o + cols) % total), STEP_MS * (HOLD_STEPS + cols));
      return () => clearInterval(timer);
    }
    let hold = 0;
    const timer = setInterval(() => {
      setOffset((o) => {
        if (o % cols === 0 && hold < HOLD_STEPS) {
          hold++;
          return o;
        }
        hold = 0;
        return (o + 1) % total;
      });
    }, STEP_MS);
    return () => clearInterval(timer);
  }, [strip, cols, reduced]);

  const columns: Column[] = strip
    ? strip.slice(offset, offset + cols)
    : renderFrame(sliding ? '' : (frames?.[0] ?? text), cols, blink ? colonOn : true);

  const gap = gapFor(p);
  const night = mode === 'night';
  const lit = mode === 'off' ? 'var(--led-off)' : 'var(--led)';
  const glow =
    mode === 'off'
      ? 'none'
      : `0 0 ${Math.round(p * 0.45)}px var(${night ? '--led-glow-night' : '--led-glow'}), ` +
        `0 0 ${Math.round(p * 1.1)}px var(${night ? '--led-halo-night' : '--led-halo'})`;

  const cells = [];
  for (let y = 0; y < ROWS; y++) {
    for (let x = 0; x < cols; x++) {
      const on = columns[x]?.[y] ?? false;
      cells.push(
        <div
          key={y * cols + x}
          style={{
            borderRadius: '50%',
            background: on ? lit : bare ? 'transparent' : 'rgba(255,255,255,0.05)',
            boxShadow: on ? glow : 'none',
            opacity: on && night ? 0.45 : 1,
          }}
        />,
      );
    }
  }

  const shown = label ?? (frames && frames.length > 0 ? frames.join(', ') : text);
  const panel = (
    <div
      {...(shown ? { role: 'img', 'aria-label': shown } : { 'aria-hidden': true })}
      style={{
        display: 'inline-flex',
        boxSizing: 'border-box',
        padding: bare ? 0 : padFor(p),
        background: bare ? 'transparent' : 'var(--plate)',
        borderRadius: Math.round(p * 0.9),
        boxShadow: bare ? 'none' : 'inset 0 0 0 1px rgba(255,255,255,0.06), inset 0 2px 14px rgba(0,0,0,0.65)',
        maxWidth: '100%',
      }}
    >
      <div style={{ display: 'grid', gridTemplateColumns: `repeat(${cols}, ${p}px)`, gridAutoRows: `${p}px`, gap }}>{cells}</div>
    </div>
  );

  if (bare) return panel;
  return (
    <div ref={fitRef} style={{ width: '100%', display: 'flex', justifyContent: 'inherit' }}>
      {panel}
    </div>
  );
}

export function Logo({ size = 4 }: { size?: number }) {
  return (
    <span className="logo">
      <Led text="K" cols={4} pixel={size} bare label="" />
      <span className="logo-word">K-Clock</span>
    </span>
  );
}
