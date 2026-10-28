import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '../lib/api';

export interface DemoLogin {
  role: string;
  email: string;
  password: string;
}

export interface DemoConfig {
  enabled: boolean;
  logins: DemoLogin[];
}

const off: DemoConfig = { enabled: false, logins: [] };

/** Whether demo mode is on, and the logins to show. A failed request counts as demo mode being off. */
export function useDemoConfig(): DemoConfig {
  const query = useQuery({
    queryKey: ['demo-config'],
    queryFn: () => apiFetch<DemoConfig>('/api/demo/config'),
    staleTime: 5 * 60 * 1000,
  });
  return query.data ?? off;
}
