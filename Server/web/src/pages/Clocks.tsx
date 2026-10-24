import { Link } from 'react-router-dom';
import { useDevices, useHistory, useSeries } from '../api/queries';
import type { Device } from '../api/types';
import { Chart } from '../chart/Chart';
import { useI18n } from '../i18n';
import { Led } from '../led/Led';
import { trendArrow } from '../led/screens';
import { ago, fixed } from '../lib/format';
import { useNow } from '../lib/useNow';
import { Icons, LoadError, Skeleton, StatusBadge } from '../ui/components';
import { clockName, hasHumidity, panelScreens } from './clockView';

function ClockPanel({ device, pixel, now }: { device: Device; pixel: number; now: number }) {
  const { t, lang } = useI18n();
  const series = useSeries(device.id, 'temp', 'day');
  const name = clockName(t, device);
  const { frames, mode } = panelScreens(device, now);
  const last = device.last;
  const stale = !device.online;

  return (
    <article className="card clock-panel">
      <header className="panel-head">
        <div className="title-row">
          <h2 className="panel-title">{name}</h2>
          <StatusBadge status={device.online ? 'online' : 'offline'} />
        </div>
        <Link to={`/clocks/${device.id}/settings`} className="icon-btn" aria-label={t.clocks.settingsAria(name)}>
          {Icons.settings()}
        </Link>
      </header>

      <Link to={`/clocks/${device.id}`} className="well" aria-label={t.clocks.openAria(name)}>
        <Led frames={frames} pixel={pixel} mode={mode} />
      </Link>

      <div className="readings-row">
        <div className="reading-cell">
          <span className="tile-label">{t.clocks.temperature}</span>
          <span className={`reading ${stale ? 'stale' : 'hot'}`}>
            {fixed(last?.temp_c)}
            <span className="unit">{t.units.celsius}</span>
          </span>
        </div>
        <div className="reading-cell">
          <span className="tile-label">{t.clocks.pressure}</span>
          <span className={`reading ${stale ? 'stale' : ''}`}>
            {last?.pressure_mmhg ?? '—'}
            <span className="unit"> {trendArrow(device.pressure_trend)}</span>
          </span>
          <span className="small muted">
            {t.units.mm}
            {device.pressure_trend ? ` · ${t.trend[device.pressure_trend]}` : ''}
          </span>
        </div>
        <div className="reading-cell">
          <span className="tile-label">{t.clocks.humidity}</span>
          {hasHumidity(device) ? (
            <span className={`reading ${stale ? 'stale' : ''}`}>
              {last?.humidity_pct ?? '—'}
              <span className="unit">%</span>
            </span>
          ) : (
            <span className="muted no-sensor">{t.common.noSensor}</span>
          )}
        </div>
      </div>

      <div className="panel-chart">
        <span className="small muted">{t.clocks.dayChart}</span>
        <Chart series={series.data} loading={series.isPending} metric="temp" height={84} timeZone={device.timezone} />
      </div>

      <footer className="panel-foot">
        <span className="small muted">
          {device.online ? t.time.updated(ago(t, lang, device.last_seen_at, now)) : t.time.lastHeard(ago(t, lang, device.last_seen_at, now))}
          {device.model && (
            <>
              {' · '}
              <span className="mono">{device.model.toUpperCase()}</span>
            </>
          )}
        </span>
        <Link to={`/clocks/${device.id}`} className="link-btn">
          {t.clocks.history} <span aria-hidden="true">→</span>
        </Link>
      </footer>
    </article>
  );
}

export function EmptyClocks() {
  const { t } = useI18n();
  return (
    <div className="card dashed empty">
      <Led text="--:--" pixel={8} mode="night" />
      <h2>{t.clocks.emptyTitle}</h2>
      <p>{t.clocks.emptyText}</p>
      <Link to="/clocks/add" className="btn btn-primary">
        {t.clocks.addTitle}
      </Link>
    </div>
  );
}

export function Clocks() {
  const { t, count } = useI18n();
  const devices = useDevices();
  const history = useHistory();
  const now = useNow();

  const list = devices.data ?? [];
  const online = list.filter((d) => d.online).length;
  // Three panels side by side need the smaller pixel to fit (HANDOFF §4.2).
  const pixel = list.length >= 3 ? 8 : 12;
  const former = history.data?.length ?? 0;

  return (
    <main className="main">
      <div className="page-head">
        <div className="titles">
          <h1>{t.clocks.title}</h1>
          {list.length > 0 && (
            <p className="muted">
              {t.clocks.summary(count(list.length, t.forms.clocks), online, list.length)} · {t.clocks.refresh}
            </p>
          )}
        </div>
      </div>

      {devices.isPending && <Skeleton height={420} />}
      {devices.isError && <LoadError onRetry={() => devices.refetch()} />}
      {devices.isSuccess && list.length === 0 && <EmptyClocks />}

      {list.length > 0 && (
        <>
          <div className="panels">
            {list.map((d) => (
              <ClockPanel key={d.id} device={d} pixel={pixel} now={now} />
            ))}
          </div>
          <Link to="/clocks/add" className="add-tile">
            <span className="add-icon">{Icons.add(22)}</span>
            <span className="add-text">
              <span className="add-title">{t.clocks.addTitle}</span>
              <span className="muted">{list.length === 1 ? t.clocks.addHintOne : t.clocks.addHintMore}</span>
            </span>
          </Link>
        </>
      )}

      {former > 0 && (
        <Link to="/history" className="quiet-link">
          <span>{t.clocks.former(former)}</span>
          <span aria-hidden="true">→</span>
        </Link>
      )}
    </main>
  );
}
