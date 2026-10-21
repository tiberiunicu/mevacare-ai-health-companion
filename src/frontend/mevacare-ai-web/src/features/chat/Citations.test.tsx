import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { safeHref } from '../../lib/links'
import type { Citation } from '../../lib/types'
import { useAuthStore } from '../../stores/auth'
import { AiSettingsPage } from '../settings/SettingsPages'
import { json, renderWithProviders, signIn, useEnglish } from '../../test/utils'
import { ChatPage } from './ChatPage'
import { Citations } from './Citations'

const citation = (overrides: Partial<Citation> = {}): Citation => ({
  number: 1,
  sourceId: 's1',
  title: 'Vitamin D',
  publisher: 'MedlinePlus',
  url: 'https://medlineplus.gov/vitamind.html',
  license: 'Public Domain (US Government work)',
  retrievedOn: '2026-10-21',
  excerpt: 'Vitamin D helps your body absorb calcium.',
  cited: true,
  ...overrides,
})

const consents = [{ type: 'AiProcessing', granted: true, required: false, policyVersion: '2026-10', grantedAtUtc: null, revokedAtUtc: null }]

function sse(...events: [string, object][]) {
  const text = events.map(([name, data]) => `event: ${name}\ndata: ${JSON.stringify(data)}\n\n`).join('')
  return new Response(
    new ReadableStream({
      start(controller) {
        controller.enqueue(new TextEncoder().encode(text))
        controller.close()
      },
    }),
    { status: 200 },
  )
}

beforeEach(async () => {
  await useEnglish()
  signIn()
})
afterEach(() => useAuthStore.getState().clear())

describe('safeHref', () => {
  it('allows http and https only', () => {
    expect(safeHref('https://example.org/a')).toBe('https://example.org/a')
    expect(safeHref('http://example.org/')).toBe('http://example.org/')
    for (const bad of ['javascript:alert(1)', 'data:text/html,hi', 'ftp://x.org', 'not a url', '']) expect(safeHref(bad)).toBeNull()
  })
})

describe('Citations', () => {
  it('lists cited sources first and labels consulted-only sources as such', () => {
    renderWithProviders(
      <Citations
        items={[
          citation({ number: 1, sourceId: 'a', title: 'Sleep Disorders', cited: false }),
          citation({ number: 2, sourceId: 'b', title: 'Vitamin D', cited: true }),
        ]}
      />,
    )

    const headings = screen.getAllByRole('heading').map((h) => h.textContent)
    expect(headings).toEqual(['Sources', 'Also consulted'])
    const lists = screen.getAllByRole('list')
    expect(lists[0]).toHaveTextContent('Vitamin D')
    expect(lists[1]).toHaveTextContent('Sleep Disorders')
  })

  it('shows publisher, licence, retrieval date, a safe external link and the passage', () => {
    renderWithProviders(<Citations items={[citation()]} />)

    expect(screen.getByText(/MedlinePlus · Licence: Public Domain/)).toBeInTheDocument()
    const link = screen.getByRole('link', { name: /Open source/ })
    expect(link).toHaveAttribute('href', 'https://medlineplus.gov/vitamind.html')
    expect(link).toHaveAttribute('rel', 'noopener noreferrer')
    expect(link).toHaveAttribute('target', '_blank')
    expect(screen.getByText('Vitamin D helps your body absorb calcium.')).toBeInTheDocument()
  })

  it('never renders a link for an unsafe URL', () => {
    renderWithProviders(<Citations items={[citation({ url: 'javascript:alert(1)' })]} />)

    expect(screen.queryByRole('link')).not.toBeInTheDocument()
    expect(screen.getByText('Vitamin D')).toBeInTheDocument()
  })
})

describe('chat with medical knowledge', () => {
  it('shows the searching tool and the sources under the streamed answer', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        if (url === '/api/consents') return json(consents)
        if (url === '/api/conversations') return json([])
        if (url === '/api/chat/stream') {
          return sse(
            ['conversation', { conversationId: 'c1', title: 'x' }],
            ['tool', { name: 'SearchMedicalKnowledge', status: 'running' }],
            ['tool', { name: 'SearchMedicalKnowledge', status: 'done' }],
            ['delta', { text: 'Vitamin D helps absorb calcium [1].' }],
            ['citations', { items: [citation()] }],
            ['done', { messageId: 'm1' }],
          )
        }
        return json([])
      }),
    )

    renderWithProviders(<ChatPage />)
    const input = await screen.findByLabelText('Ask anything about your wellness…')
    await waitFor(() => expect(input).toBeEnabled())
    await userEvent.type(input, 'Why is vitamin D important?{Enter}')

    expect(await screen.findByText('Searching approved medical sources')).toBeInTheDocument()
    expect(await screen.findByRole('link', { name: /Open source/ })).toHaveAttribute('href', 'https://medlineplus.gov/vitamind.html')
    expect(screen.getByText('Sources')).toBeInTheDocument()
  })

  it('restores the sources of earlier answers when a conversation is reopened', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        if (url === '/api/consents') return json(consents)
        if (url === '/api/conversations') return json([{ id: 'c1', title: 'Vitamin D', lastMessageAtUtc: new Date().toISOString() }])
        if (url === '/api/conversations/c1/messages') {
          return json([
            { id: 'u1', sequence: 1, role: 'User', content: 'Why vitamin D?', status: 'Complete', isSafetyResponse: false, createdAtUtc: '2026-10-21T10:00:00Z', citations: [] },
            { id: 'a1', sequence: 2, role: 'Assistant', content: 'It helps absorb calcium [1].', status: 'Complete', isSafetyResponse: false, createdAtUtc: '2026-10-21T10:00:01Z', citations: [citation()] },
          ])
        }
        return json([])
      }),
    )

    renderWithProviders(<ChatPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Vitamin D' }))

    expect(await screen.findByRole('link', { name: /Open source/ })).toBeInTheDocument()
  })
})

describe('AiSettingsPage knowledge sources', () => {
  it('lists the approved sources and their licences, or says that none exist', async () => {
    let sources: unknown[] = [
      { id: 's1', title: 'Vitamin D', publisher: 'MedlinePlus', url: 'https://medlineplus.gov/vitamind.html', license: 'CC0-1.0', licenseUrl: null, language: 'en', retrievedOn: '2026-10-21', version: 1, chunkCount: 4 },
    ]
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => (String(input) === '/api/consents' ? json(consents) : json(sources))),
    )

    const { unmount } = renderWithProviders(<AiSettingsPage />)
    expect(await screen.findByRole('link', { name: 'Vitamin D' })).toHaveAttribute('href', 'https://medlineplus.gov/vitamind.html')
    expect(screen.getByText(/MedlinePlus · CC0-1.0 · retrieved .* · 4 passages/)).toBeInTheDocument()
    unmount()

    sources = []
    renderWithProviders(<AiSettingsPage />)
    expect(await screen.findByText(/No approved sources are indexed yet/)).toBeInTheDocument()
  })
})
