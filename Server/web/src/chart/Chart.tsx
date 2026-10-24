import { useId } from 'react';
import type { Metric, Period, Series } from '../api/types';
import { useI18n } from '../i18n';
import type { Dictionary } from '../i18n/uk';
import { Skeleton } from '../ui/components';
import { layout, VIEW_WIDTH } from './geometry';

const DECIMALS: Record<Metric, number> = { temp: 1, pressure: 0, humidity: 0 };
const UNITS: Record<Metric, string> = { temp: '°', pressure: '', humidity: '%' };

/** Five labels across the window, in the clock's own zone; the day chart ends on "now". */
function xLabels(t: Dictionary, period: Period, from: number, to: number, timeZone: string, live: boolean): string[] {
  const options: Record<Period, Intl.DateTimeFormatOptions> = {
    day: { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' },
    week: { weekday: 'short' },
    month: { day: 'numeric', month: 'short' },
    year: { month: 'short' },
  };
  const fmt = new Intl.DateTimeFormat(t.locale, { ...options[period], timeZone });
  const count = period === 'week' ? 7 : 5;
  return Array.from({ length: count }, (_, i) => {
    if (live && i === count - 1) return t.chart.now;
    return fmt.format((from + ((to - from) * i) / (count - 1)) * 1000);
  });
}

/**
 * design/Chart.dc.html with real data: a 2 px accent line over a fading fill, three y labels,
 * dashed grid, shaded gaps labelled "no data", and a ringed dot on the latest value.
 * `live` says the window ends now, which is true of a current clock and not of a former one.
 */
export function Chart({
  series,
  metric,
  height = 180,
  timeZone,
  live = true,
  loading,
}: {
  series: Series | undefined;
  metric: Metric;
  height?: number;
  timeZone: string;
  live?: boolean;
  loading?: boolean;
}) {
  const { t } = useI18n();
  const gradient = useId();

  if (!series) return loading ? <Skeleton height={height + 24} /> : null;

  const g = layout(series.points, series.from, series.to, height);
  if (!g) {
    return (
      <div className="chart chart-empty muted" style={{ height }}>
        {t.chart.empty}
      </div>
    );
  }

  const fmt = (v: number) => v.toFixed(DECIMALS[metric]) + UNITS[metric];
  const yLabels = [g.max, (g.max + g.min) / 2, g.min].map((v) => ({ top: Math.round(g.y(v)), label: fmt(v) }));
  const aria = t.chart.aria(t.chart.names[metric] ?? metric, fmt(g.min), fmt(g.max), g.gaps.length > 0);

  return (
    <div className="chart">
      <div className="chart-body" style={{ height }}>
        <div className="chart-y" aria-hidden="true">
          {yLabels.map((l, i) => (
            <span key={i} style={{ top: l.top }}>
              {l.label}
            </span>
          ))}
        </div>
        <div className="chart-plot">
          {yLabels.map((l, i) => (
            <div key={i} className="chart-grid" style={{ top: l.top }} />
          ))}
          {/* Colours go through style, not attributes: var() is CSS and an SVG presentation
              attribute does not resolve it in every browser. */}
          <svg viewBox={`0 0 ${VIEW_WIDTH} ${height}`} preserveAspectRatio="none" role="img" aria-label={aria}>
            <defs>
              <linearGradient id={gradient} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0" style={{ stopColor: 'var(--accent)', stopOpacity: 0.22 }} />
                <stop offset="1" style={{ stopColor: 'var(--accent)', stopOpacity: 0 }} />
              </linearGradient>
            </defs>
            <path d={g.area} fill={`url(#${gradient})`} />
            <path
              d={g.line}
              fill="none"
              style={{ stroke: 'var(--accent)' }}
              strokeWidth="2"
              strokeLinejoin="round"
              strokeLinecap="round"
              vectorEffect="non-scaling-stroke"
            />
            <path d={g.dots} fill="none" style={{ stroke: 'var(--accent)' }} strokeWidth="5" strokeLinecap="round" vectorEffect="non-scaling-stroke" />
          </svg>
          {g.gaps.map((gap, i) => (
            <div key={i} className="chart-gap" style={{ left: `${gap.left}%`, width: `${gap.width}%` }}>
              {gap.width > 6 ? t.chart.noData : null}
            </div>
          ))}
          {g.last && <div className="chart-last" style={{ left: `${g.last.x}%`, top: g.last.y }} aria-hidden="true" />}
        </div>
      </div>
      <div className="chart-x" aria-hidden="true">
        {xLabels(t, series.period, series.from, series.to, timeZone, live).map((label, i) => (
          <span key={i}>{label}</span>
        ))}
      </div>
    </div>
  );
}
