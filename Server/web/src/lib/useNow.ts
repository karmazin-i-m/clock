import { useEffect, useState } from 'react';

/** The current time, re-read every `everyMs`, for clocks and "N minutes ago" labels. */
export function useNow(everyMs = 10_000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), everyMs);
    return () => clearInterval(timer);
  }, [everyMs]);
  return now;
}
