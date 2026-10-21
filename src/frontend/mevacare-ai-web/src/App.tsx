import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createBrowserRouter, Navigate, RouterProvider } from 'react-router-dom'
import { AppShell } from './components/AppShell'
import { LoginPage, RegisterPage, RequireAuth } from './features/auth/AuthPages'
import { useBootstrapSession } from './features/auth/hooks'
import { ChatPage } from './features/chat/ChatPage'
import { DailySummaryPage, FoodJournalPage, NutritionGoalsPage } from './features/nutrition/NutritionPages'
import { usePreferencesSync } from './features/profile/hooks'
import { ProfilePage } from './features/profile/ProfilePage'
import { AiSettingsPage, PreferencesPage, PrivacyPage } from './features/settings/SettingsPages'
import { ComingSoon } from './pages/ComingSoon'
import { Dashboard } from './pages/Dashboard'

const HEALTH_NAV = [
  { to: '/health/weight', labelKey: 'nav.weight' },
  { to: '/health/sleep', labelKey: 'nav.sleep' },
  { to: '/health/activity', labelKey: 'nav.activity' },
]

function Authenticated() {
  usePreferencesSync()
  return <AppShell />
}

const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  { path: '/register', element: <RegisterPage /> },
  {
    element: <RequireAuth />,
    children: [
      {
        element: <Authenticated />,
        children: [
          { path: '/', element: <Dashboard /> },
          { path: '/companion', element: <ChatPage /> },
          { path: '/nutrition', element: <Navigate to="/nutrition/journal" replace /> },
          { path: '/nutrition/journal', element: <FoodJournalPage /> },
          { path: '/nutrition/summary', element: <DailySummaryPage /> },
          { path: '/nutrition/goals', element: <NutritionGoalsPage /> },
          { path: '/health', element: <Navigate to="/health/weight" replace /> },
          { path: '/health/weight', element: <ComingSoon titleKey="nav.weight" phaseKey="phases.measurements" subNav={HEALTH_NAV} /> },
          { path: '/health/sleep', element: <ComingSoon titleKey="nav.sleep" phaseKey="phases.measurements" subNav={HEALTH_NAV} /> },
          { path: '/health/activity', element: <ComingSoon titleKey="nav.activity" phaseKey="phases.measurements" subNav={HEALTH_NAV} /> },
          { path: '/devices', element: <ComingSoon titleKey="nav.devices" phaseKey="phases.devices" /> },
          { path: '/settings', element: <Navigate to="/settings/profile" replace /> },
          { path: '/settings/profile', element: <ProfilePage /> },
          { path: '/settings/preferences', element: <PreferencesPage /> },
          { path: '/settings/privacy', element: <PrivacyPage /> },
          { path: '/settings/ai', element: <AiSettingsPage /> },
        ],
      },
    ],
  },
  { path: '*', element: <Navigate to="/" replace /> },
])

const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 30_000, refetchOnWindowFocus: false } },
})

export default function App() {
  useBootstrapSession()
  return (
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  )
}
