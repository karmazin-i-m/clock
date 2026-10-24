// The browser API's shapes, as Server/src/KClock.Api/Accounts writes them. snake_case on the
// wire, kept here as is rather than mapped, so a field can be grepped across both sides.

export type Trend = 'up' | 'down' | 'flat';
export type Metric = 'temp' | 'pressure' | 'humidity';
export type Period = 'day' | 'week' | 'month' | 'year';
export type Model = 'bme280' | 'bmp280';

export type Me = {
  id: string;
  email: string | null;
  display_name: string | null;
  picture: string | null;
  created_at: string;
};

export type Reading = {
  ts: string;
  temp_c: number | null;
  pressure_mmhg: number | null;
  humidity_pct: number | null;
};

export type Device = {
  id: number;
  hardware_id: string;
  profile: string;
  model: Model | null;
  display_name: string | null;
  timezone: string;
  bound_at: string;
  online: boolean;
  fw_version: string | null;
  last_seen_at: string | null;
  last: Reading | null;
  pressure_trend: Trend | null;
  pressure_delta_3h: number | null;
};

export type Summary = {
  from: string;
  to: string;
  temp_min_c: number | null;
  temp_max_c: number | null;
  humidity_min_pct: number | null;
  humidity_max_pct: number | null;
  readings: number;
};

/** [epoch seconds, value]; a null value is a gap the line must not cross. */
export type Point = [number, number | null];

export type Series = {
  metric: Metric;
  period: Period;
  step_s: number;
  from: number;
  to: number;
  points: Point[];
};

export type FormerClock = {
  binding_id: number;
  device_id: number;
  model: Model | null;
  display_name: string | null;
  timezone: string;
  bound_at: string;
  unbound_at: string;
  unbound_reason: string | null;
};

export type FormerClockDetail = {
  clock: FormerClock;
  last: Reading | null;
  summary: Summary;
};

export type CodeStatus = 'active' | 'used' | 'expired';

export type CreatedCode = { id: string; code: string; expires_at: string };

export type CodeListItem = {
  id: string;
  status: CodeStatus;
  created_at: string;
  expires_at: string;
  consumed_at: string | null;
  consumed_device_id: number | null;
};

/** What the clock's own panel does, held by the cloud and sent down with the next batch. */
export type DisplaySettings = {
  dim_from: number;
  dim_until: number;
  marquee_minutes: number;
  /** Bumped by every change that reaches the clock: these values, or the time zone. */
  version: number;
  /** The version the clock last said it holds; behind `version` until it has applied it. */
  applied_version: number | null;
};
