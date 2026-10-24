import { useI18n } from '../i18n';
import { PRESETS, hh, hoursDimmed, modeOf, segments, step, type Night } from './model';

const pct = (h: number) => (h / 24) * 100;

/**
 * design/NightSlider.dc.html: the 0–24 track with the dimmed stretch filled (two pieces when
 * it wraps midnight), presets, and −/+ steppers for each end. The steppers are the control;
 * the track only shows the result, so everything works from the keyboard and by touch.
 */
export function NightSlider({ value, onChange }: { value: Night; onChange: (next: Night) => void }) {
  const { t } = useI18n();
  const mode = modeOf(value);

  const summary =
    mode === 'never'
      ? t.settings.never
      : mode === 'always'
        ? t.settings.always
        : `${hh(value.from)} → ${hh(value.until)} · ${hoursDimmed(value)} ${t.settings.hoursShort}`;

  const presetLabel = (p: Night) =>
    modeOf(p) === 'never' ? t.settings.presetNever : modeOf(p) === 'always' ? t.settings.presetAlways : `${hh(p.from)}–${hh(p.until)}`;
  const isSelected = (p: Night) => (modeOf(p) === 'range' ? p.from === value.from && p.until === value.until : modeOf(p) === mode);

  const steppers = [
    { end: 'from' as const, label: t.settings.from, earlier: t.settings.fromEarlier, later: t.settings.fromLater },
    { end: 'until' as const, label: t.settings.to, earlier: t.settings.toEarlier, later: t.settings.toLater },
  ];

  return (
    <div className="night">
      <div className="night-head">
        <span className="night-title">{t.settings.night}</span>
        <span className="mono night-summary" aria-live="polite">
          {summary}
        </span>
      </div>

      <div className="night-track" aria-hidden="true">
        <div className="night-rail" />
        {segments(value).map(([a, b]) => (
          <div key={a} className="night-fill" style={{ left: `${pct(a)}%`, width: `${pct(b - a)}%` }} />
        ))}
        {mode === 'range' &&
          [value.from, value.until].map((h, i) => <div key={i} className="night-thumb" style={{ left: `${pct(h)}%` }} />)}
        {[0, 3, 6, 9, 12, 15, 18, 21, 24].map((h) => (
          <span key={h} className="night-tick mono" style={{ left: `${pct(h)}%` }}>
            {h}
          </span>
        ))}
      </div>

      <div className="chips">
        {PRESETS.map((p) => (
          <button key={presetLabel(p)} type="button" className="chip" aria-pressed={isSelected(p)} onClick={() => onChange(p)}>
            {presetLabel(p)}
          </button>
        ))}
      </div>

      <div className="night-steppers">
        {steppers.map((s) => (
          <div key={s.end} className="stepper">
            <span className="muted small">{s.label}</span>
            <div className="stepper-controls">
              <button type="button" aria-label={s.earlier} onClick={() => onChange(step(value, s.end, -1))}>
                −
              </button>
              <span className="mono">{mode === 'range' ? hh(value[s.end]) : '—'}</span>
              <button type="button" aria-label={s.later} onClick={() => onChange(step(value, s.end, 1))}>
                +
              </button>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
