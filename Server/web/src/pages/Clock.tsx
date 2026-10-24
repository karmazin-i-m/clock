import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useDevice, useSeries, useSummary } from '../api/queries';
import { ApiError } from '../api/client';
import type { Device, Metric, Period, Series } from '../api/types';
import { Chart } from '../chart/Chart';
import { useI18n } from '../i18n';
import { Led } from '../led/Led';
import { humidityScreen, pressureScreen, temperatureScreen, timeScreen, trendArrow } from '../led/screens';
import { ago, clockTime, dateTime, fixed, signed, span } from '../lib/format';
import { useNow } from '../lib/useNow';
import { Icons, LoadError, Segmented, Skeleton, StatusBadge } from '../ui/components';
import { clockName, hasHumidity } from './clockView';

type Screen = 'time' | 'temp' | 'press' | 'hum';

export function ClockHeader({ device, current }: { device: Device; current: 'readings' | 'settings' }) {
  const { t } = useI18n();
  const name = clockName(t, device);
  return (
    <>
      <div className="crumbs">
        <Link to="/clocks">{t.nav.clocks}</Link>
        <span className="sep" aria-hidden="true">
          /
        </span>
        {current === 'readings' ? (
          <span aria-current="page">{name}</span>
        ) : (
          <>
            <Link to={`/clocks/${device.id}`}>{name}</Link>
            <span className="sep" aria-hidden="true">
              /
            </span>
            <span aria-current="page">{t.clock.settings}</span>
          </>
        )}
      </div>
      <div className="page-head">
        <div className="title-row">
          <h1>{name}</h1>
          <StatusBadge status={device.online ? 'online' : 'offline'} />
          {device.model && <span className="mono small muted">{device.model.toUpperCase()}</span>}
        </div>
        <nav aria-label={t.clock.tabs} className="segmented">
          <Link to={`/clocks/${device.id}`} aria-current={current === 'readings' ? 'page' : undefined}>
            {t.clock.readings}
          </Link>
          <Link to={`/clocks/${device.id}/settings`} aria-current={current === 'settings' ? 'page' : undefined}>
            {Icons.settings(16)}
            {t.clock.settings}
          </Link>
        </nav>
      </div>
    </>
  );
}

export function NotFoundNote({ text }: { text: string }) {
  const { t } = useI18n();
  return (
    <div className="card empty">
      <p>{text}</p>
      <Link to="/clocks" className="btn btn-secondary">
        {t.notFound.back}
      </Link>
    </div>
  );
}

/** Three charts under one period switch; shared by a current clock and a former one. */
export function ChartsSection({
  title,
  period,
  periods,
  onPeriod,
  series,
  withHumidity,
  timeZone,
  live,
}: {
  title: string;
  period: Period;
  periods: readonly Period[];
  onPeriod: (p: Period) => void;
  series: (metric: Metric) => { data: Series | undefined; isPending: boolean };
  withHumidity: boolean;
  timeZone: string;
  live: boolean;
}) {
  const { t } = useI18n();
  const temp = series('temp');
  const range = (s: Series | undefined) => (!s ? '' : period === 'day' && live ? t.clock.lastDay : span(t, s.from, s.to, timeZone));

  const charts: { metric: Metric; label: string }[] = [
    { metric: 'temp', label: t.clock.tempChart },
    { metric: 'pressure', label: t.clock.pressChart },
    ...(withHumidity ? [{ metric: 'humidity' as const, label: t.clock.humChart }] : []),
  ];

  return (
    <section className="charts" aria-labelledby="charts-title">
      <div className="page-head">
        <h2 id="charts-title" className="section-title">
          {title}
        </h2>
        <Segmented label={t.clock.period} value={period} options={periods.map((id) => ({ id, label: t.clock.periods[id] }))} onChange={onPeriod} />
      </div>
      {charts.map(({ metric, label }) => (
        <ChartCard key={metric} label={label} range={range(temp.data)} query={series(metric)} metric={metric} timeZone={timeZone} live={live} />
      ))}
      {!withHumidity && (
        <div className="card tight dashed">
          <span className="chart-card-title">{t.clock.noHumidityTitle}</span>
          <span className="reading stale">—</span>
          <span className="muted small">{t.clock.noHumidity}</span>
        </div>
      )}
      <p className="small muted">{t.clock.gapsNote}</p>
    </section>
  );
}

function ChartCard({
  label,
  range,
  query,
  metric,
  timeZone,
  live,
}: {
  label: string;
  range: string;
  query: { data: Series | undefined; isPending: boolean };
  metric: Metric;
  timeZone: string;
  live: boolean;
}) {
  return (
    <div className="card chart-card">
      <div className="chart-card-head">
        <span className="chart-card-title">{label}</span>
        <span className="small muted">{range}</span>
      </div>
      <Chart series={query.data} loading={query.isPending} metric={metric} timeZone={timeZone} live={live} />
    </div>
  );
}

export function Clock() {
  const id = Number(useParams().id);
  const { t, lang } = useI18n();
  const device = useDevice(id);
  const summary = useSummary(id);
  const [period, setPeriod] = useState<Period>('day');
  const [screen, setScreen] = useState<Screen>('time');
  const now = useNow();
  const humidity = device.data ? hasHumidity(device.data) : true;

  // Hooks run unconditionally; humidity is fetched only for a clock that has the sensor.
  const temp = useSeries(id, 'temp', period);
  const pressure = useSeries(id, 'pressure', period);
  const hum = useSeries(id, 'humidity', period, humidity);
  const byMetric = { temp, pressure, humidity: hum };

  if (device.isPending) {
    return (
      <main className="main">
        <Skeleton height={520} />
      </main>
    );
  }
  if (device.error instanceof ApiError && device.error.status === 404) {
    return (
      <main className="main">
        <NotFoundNote text={t.clock.notFound} />
      </main>
    );
  }
  if (device.isError) {
    return (
      <main className="main">
        <LoadError onRetry={() => device.refetch()} />
      </main>
    );
  }

  const d = device.data;
  const last = d.last;
  const s = summary.data;
  const screens: { id: Screen; label: string; text: string }[] = [
    { id: 'time', label: t.clock.screens.time, text: timeScreen(new Date(now), d.timezone) },
    { id: 'temp', label: t.clock.screens.temp, text: temperatureScreen(last?.temp_c) },
    { id: 'press', label: t.clock.screens.press, text: pressureScreen(last?.pressure_mmhg, d.pressure_trend) },
    ...(humidity ? [{ id: 'hum' as const, label: t.clock.screens.hum, text: humidityScreen(last?.humidity_pct) }] : []),
  ];
  const shown = screens.find((x) => x.id === screen) ?? screens[0]!;
  const lastMs = last ? Date.parse(last.ts) : null;
  const stale = !d.online;

  return (
    <main className="main narrow">
      <ClockHeader device={d} current="readings" />

      {!d.online && d.last_seen_at && (
        <div className="note neutral" role="status">
          {Icons.offline()}
          <span>
            <b>{t.clock.offlineTitle(clockName(t, d), dateTime(t, d.last_seen_at, d.timezone))}</b>
            <br />
            <span className="muted">{t.clock.offlineText}</span>
          </span>
        </div>
      )}

      <section className="card hero">
        <div className="hero-display">
          <div className="well">
            <Led text={shown.text} pixel={16} blink={shown.id === 'time' && d.online} mode={d.online ? 'normal' : 'off'} />
          </div>
          <div className="chips" role="group" aria-label={t.clock.screen}>
            {screens.map((x) => (
              <button key={x.id} type="button" className="chip" aria-pressed={x.id === shown.id} onClick={() => setScreen(x.id)}>
                {x.label}
              </button>
            ))}
          </div>
        </div>
        <div className="tiles hero-tiles">
          <div>
            <span className="tile-label">{t.clocks.temperature}</span>
            <span className={`reading big ${stale ? 'stale' : 'hot'}`}>
              {fixed(last?.temp_c)}
              <span className="unit"> {t.units.celsius}</span>
            </span>
            {s?.temp_min_c != null && <span className="small muted">{t.clock.dayRange(fixed(s.temp_min_c), fixed(s.temp_max_c))}</span>}
          </div>
          <div>
            <span className="tile-label">{t.clocks.pressure}</span>
            <span className={`reading big ${stale ? 'stale' : ''}`}>
              {last?.pressure_mmhg ?? '—'}
              <span className="unit"> {t.units.mm}</span>
            </span>
            {d.pressure_trend && d.pressure_delta_3h != null && (
              <span className="small muted">
                {t.clock.trendLine(trendArrow(d.pressure_trend), t.trend[d.pressure_trend] ?? '', signed(d.pressure_delta_3h))}
              </span>
            )}
          </div>
          <div>
            <span className="tile-label">{t.clocks.humidity}</span>
            {humidity ? (
              <>
                <span className={`reading big ${stale ? 'stale' : ''}`}>
                  {last?.humidity_pct ?? '—'}
                  <span className="unit"> %</span>
                </span>
                {s?.humidity_min_pct != null && (
                  <span className="small muted">{t.clock.dayRange(String(s.humidity_min_pct), String(s.humidity_max_pct))}</span>
                )}
              </>
            ) : (
              <span className="muted no-sensor">{t.common.noSensor}</span>
            )}
          </div>
          <div>
            <span className="tile-label">{t.clock.updated}</span>
            <span className="reading big">{lastMs ? clockTime(t, lastMs, d.timezone) : '—'}</span>
            <span className="small muted">
              {lastMs
                ? d.online
                  ? t.clock.next(ago(t, lang, last!.ts, now), clockTime(t, lastMs + 60_000, d.timezone))
                  : ago(t, lang, last!.ts, now)
                : t.clock.noData}
            </span>
          </div>
        </div>
      </section>

      <ChartsSection
        title={t.clock.history}
        period={period}
        periods={['day', 'week', 'month', 'year']}
        onPeriod={setPeriod}
        series={(metric) => byMetric[metric]}
        withHumidity={humidity}
        timeZone={d.timezone}
        live
      />
    </main>
  );
}
