import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createBrowserRouter, Navigate, RouterProvider } from 'react-router-dom'
import { AppShell } from './components/AppShell'
import { LoginPage, RegisterPage, RequireAuth } from './features/auth/AuthPages'
import { useBootstrapSession } from './features/auth/hooks'
import { ChatPage } from './features/chat/ChatPage'
import { DevicesPage } from './features/devices/DevicesPage'
import { ActivityPage, SleepPage, WeightPage } from './features/health/HealthPages'
import { InsightsPage } from './features/insights/InsightsPage'
import { DailySummaryPage, FoodJournalPage, NutritionGoalsPage } from './features/nutrition/NutritionPages'
import { usePreferencesSync } from './features/profile/hooks'
import { ProfilePage } from './features/profile/ProfilePage'
import { AiSettingsPage, PreferencesPage, PrivacyPage } from './features/settings/SettingsPages'
import { Dashboard } from './pages/Dashboard'

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
          { path: '/health/weight', element: <WeightPage /> },
          { path: '/health/sleep', element: <SleepPage /> },
          { path: '/health/activity', element: <ActivityPage /> },
          { path: '/health/insights', element: <InsightsPage /> },
          { path: '/devices', element: <DevicesPage /> },
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
