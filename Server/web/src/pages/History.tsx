import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ApiError } from '../api/client';
import { useFormerClock, useFormerSeries, useHistory } from '../api/queries';
import type { FormerClock, Period } from '../api/types';
import { useI18n } from '../i18n';
import { date } from '../lib/format';
import { Icons, LoadError, Skeleton, StatusBadge } from '../ui/components';
import { ChartsSection } from './Clock';
import { clockName, hasHumidity } from './clockView';

function useReason() {
  const { t } = useI18n();
  return (clock: FormerClock) => t.history.reason[clock.unbound_reason ?? ''] ?? t.history.reasonOther;
}

function FormerDetail({ bindingId }: { bindingId: number }) {
  const { t } = useI18n();
  const detail = useFormerClock(bindingId);
  const [period, setPeriod] = useState<Period>('month');
  const humidity = detail.data ? hasHumidity(detail.data.clock) : true;
  const temp = useFormerSeries(bindingId, 'temp', period);
  const pressure = useFormerSeries(bindingId, 'pressure', period);
  const hum = useFormerSeries(bindingId, 'humidity', period, humidity);
  const byMetric = { temp, pressure, humidity: hum };

  if (detail.isPending) return <Skeleton height={480} />;
  if (detail.error instanceof ApiError && detail.error.status === 404) {
    return <div className="card empty"><p>{t.history.notFound}</p></div>;
  }
  if (detail.isError) return <LoadError onRetry={() => detail.refetch()} />;

  const c = detail.data.clock;
  return (
    <section id="former" className="card former" aria-labelledby="former-title">
      <div className="titles">
        <h2 id="former-title" className="former-title">
          {clockName(t, c)}
        </h2>
        <span className="muted">{t.history.span(date(t, c.bound_at, c.timezone), date(t, c.unbound_at, c.timezone))}</span>
      </div>
      <div className="note" role="note">
        {Icons.lock()}
        <span>{t.history.note}</span>
      </div>
      <ChartsSection
        title={t.clock.history}
        period={period}
        periods={['week', 'month', 'year']}
        onPeriod={setPeriod}
        series={(metric) => byMetric[metric]}
        withHumidity={humidity}
        timeZone={c.timezone}
        live={false}
      />
    </section>
  );
}

export function History() {
  const { t } = useI18n();
  const params = useParams();
  const selected = params.bindingId ? Number(params.bindingId) : null;
  const history = useHistory();
  const reason = useReason();

  return (
    <main className="main">
      <div className="titles page-titles">
        <h1>{t.history.title}</h1>
        <p className="lead">{t.history.lead}</p>
      </div>

      {history.isPending && <Skeleton height={120} />}
      {history.isError && <LoadError onRetry={() => history.refetch()} />}
      {history.isSuccess && history.data.length === 0 && (
        <div className="card dashed empty">
          <p>{t.history.empty}</p>
        </div>
      )}

      {history.isSuccess && history.data.length > 0 && (
        <div className="former-list">
          {history.data.map((c) => (
            <Link
              key={c.binding_id}
              to={`/history/${c.binding_id}`}
              className="card tight former-card"
              aria-current={c.binding_id === selected ? 'true' : undefined}
            >
              <div className="panel-head">
                <span className="former-name">{clockName(t, c)}</span>
                <StatusBadge status="readonly" />
              </div>
              <span className="muted small">
                {reason(c)} · {date(t, c.unbound_at, c.timezone)}
              </span>
              <span className="mono small muted">
                {date(t, c.bound_at, c.timezone)} – {date(t, c.unbound_at, c.timezone)}
                {c.model ? ` · ${c.model.toUpperCase()}` : ''}
              </span>
            </Link>
          ))}
        </div>
      )}

      {selected !== null && <FormerDetail key={selected} bindingId={selected} />}
    </main>
  );
}
