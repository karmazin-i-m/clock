import { Navigate, Route, Routes } from 'react-router-dom';
import { ApiError } from './api/client';
import { useMe } from './api/queries';
import { Layout } from './ui/Layout';
import { LoadError, Skeleton } from './ui/components';
import { Add } from './pages/Add';
import { Clock } from './pages/Clock';
import { Clocks } from './pages/Clocks';
import { ClockSettings } from './pages/ClockSettings';
import { History } from './pages/History';
import { Login } from './pages/Login';
import { NotFound } from './pages/NotFound';
import { Profile } from './pages/Profile';

/**
 * Every page but sign-in waits for /api/me. A missing session never gets this far: the API
 * client turns the 401 into a full-page trip to /login, carrying the page to come back to.
 */
function Authenticated() {
  const me = useMe();
  if (me.isSuccess) return <Layout />;
  // A 401 is already on its way to /login; anything else is the server, and says so.
  const unauthorized = me.error instanceof ApiError && me.error.status === 401;
  return (
    <div className="shell">
      <main className="main">{me.isError && !unauthorized ? <LoadError onRetry={() => me.refetch()} /> : <Skeleton height={320} />}</main>
    </div>
  );
}

// The routes of HANDOFF §5. /clocks/add is declared before /clocks/:id so "add" is never
// read as an id.
export function App() {
  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route element={<Authenticated />}>
        <Route path="/" element={<Navigate to="/clocks" replace />} />
        <Route path="/clocks" element={<Clocks />} />
        <Route path="/clocks/add" element={<Add />} />
        <Route path="/clocks/:id" element={<Clock />} />
        <Route path="/clocks/:id/settings" element={<ClockSettings />} />
        <Route path="/history" element={<History />} />
        <Route path="/history/:bindingId" element={<History />} />
        <Route path="/profile" element={<Profile />} />
        <Route path="*" element={<NotFound />} />
      </Route>
    </Routes>
  );
}
