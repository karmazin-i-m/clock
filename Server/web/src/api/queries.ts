import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from './client';
import type {
  CodeListItem,
  CreatedCode,
  Device,
  DisplaySettings,
  FormerClock,
  FormerClockDetail,
  Me,
  Metric,
  Period,
  Series,
  Summary,
} from './types';

// Readings arrive in batches about once a minute (HANDOFF §8 rule 2), so that is how often
// anything showing them asks again. Polling faster would only fetch the same batch twice.
export const READINGS_REFRESH_MS = 60_000;

export const useMe = () => useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/api/me'), staleTime: Infinity });

export const useDevices = () =>
  useQuery({ queryKey: ['devices'], queryFn: () => api<Device[]>('/api/devices'), refetchInterval: READINGS_REFRESH_MS });

export const useDevice = (id: number) =>
  useQuery({ queryKey: ['devices', id], queryFn: () => api<Device>(`/api/devices/${id}`), refetchInterval: READINGS_REFRESH_MS });

export const useSummary = (id: number) =>
  useQuery({
    queryKey: ['devices', id, 'summary'],
    queryFn: () => api<Summary>(`/api/devices/${id}/summary`),
    refetchInterval: READINGS_REFRESH_MS,
  });

export const useSeries = (id: number, metric: Metric, period: Period, enabled = true) =>
  useQuery({
    queryKey: ['devices', id, 'series', metric, period],
    queryFn: () => api<Series>(`/api/devices/${id}/series?metric=${metric}&period=${period}`),
    refetchInterval: READINGS_REFRESH_MS,
    enabled,
  });

export const useHistory = () => useQuery({ queryKey: ['history'], queryFn: () => api<FormerClock[]>('/api/history') });

export const useFormerClock = (bindingId: number) =>
  useQuery({ queryKey: ['history', bindingId], queryFn: () => api<FormerClockDetail>(`/api/history/${bindingId}`) });

// A former clock's data no longer changes, so it is never refetched.
export const useFormerSeries = (bindingId: number, metric: Metric, period: Period, enabled = true) =>
  useQuery({
    queryKey: ['history', bindingId, 'series', metric, period],
    queryFn: () => api<Series>(`/api/history/${bindingId}/series?metric=${metric}&period=${period}`),
    staleTime: Infinity,
    enabled,
  });

export const useCodes = (enabled: boolean) =>
  useQuery({
    queryKey: ['codes'],
    queryFn: () => api<CodeListItem[]>('/api/enrollment-codes'),
    refetchInterval: 5_000,
    enabled,
  });

export const createCode = () => api<CreatedCode>('/api/enrollment-codes', { method: 'POST' });

export function useUnbind(id: number) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => api<void>(`/api/devices/${id}/binding`, { method: 'DELETE' }),
    onSuccess: () => Promise.all([
      client.invalidateQueries({ queryKey: ['devices'] }),
      client.invalidateQueries({ queryKey: ['history'] }),
    ]),
  });
}

export const useSettings = (id: number) =>
  useQuery({
    queryKey: ['devices', id, 'settings'],
    queryFn: () => api<DisplaySettings>(`/api/devices/${id}/settings`),
    // Polled so "the clock has not confirmed yet" clears by itself once the next batch lands.
    refetchInterval: READINGS_REFRESH_MS,
  });

export function useSaveClock(id: number) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async (body: {
      details?: { display_name?: string; timezone?: string };
      display?: Pick<DisplaySettings, 'dim_from' | 'dim_until' | 'marquee_minutes'>;
    }) => {
      if (body.details) await api<void>(`/api/devices/${id}`, { method: 'PATCH', json: body.details });
      if (body.display) await api<void>(`/api/devices/${id}/settings`, { method: 'PUT', json: body.display });
    },
    onSettled: () => client.invalidateQueries({ queryKey: ['devices'] }),
  });
}

export const signOut = () => api<void>('/api/auth/signout', { method: 'POST' });

export const deleteAccount = () => api<void>('/api/me', { method: 'DELETE' });
