import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useAuthStore } from '../../stores/auth'
import { json, renderWithProviders, signIn, useEnglish } from '../../test/utils'
import { useChat } from './chatSession'
import { ChatDock } from './ChatDock'

const consents = (granted: boolean) => [
  { type: 'AiProcessing', granted, required: false, policyVersion: '2026-10', grantedAtUtc: null, revokedAtUtc: null },
]

function sse(...events: [string, object][]) {
  const text = events.map(([name, data]) => `event: ${name}\ndata: ${JSON.stringify(data)}\n\n`).join('')
  const body = new ReadableStream({
    start(controller) {
      controller.enqueue(new TextEncoder().encode(text))
      controller.close()
    },
  })
  return new Response(body, { status: 200 })
}

function setup(granted = true) {
  const calls: { url: string; init?: RequestInit }[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ url, init })
      if (url === '/api/consents') return json(consents(granted))
      if (url === '/api/chat/stream') {
        return sse(
          ['conversation', { conversationId: 'c1', title: 'x' }],
          ['delta', { text: 'Prepared your breakfast.' }],
          ['done', { messageId: 'm1' }],
        )
      }
      return json([])
    }),
  )
  return calls
}

/** Stands in for a page (the food journal) that hands a sentence to the assistant. */
function AskButton() {
  const { ask } = useChat()
  return <button onClick={() => void ask('two eggs for breakfast')}>hand over</button>
}

beforeEach(async () => {
  await useEnglish()
  signIn()
})
afterEach(() => useAuthStore.getState().clear())

describe('ChatDock', () => {
  it('opens from the corner button, closes with the close button and with Escape', async () => {
    setup()
    renderWithProviders(<ChatDock />)

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Ask or log' }))
    expect(screen.getByRole('dialog', { name: 'Assistant' })).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Close the assistant' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Ask or log' }))
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('is not shown on the full chat page, which already shows the same conversation', () => {
    setup()
    renderWithProviders(<ChatDock />, { route: '/companion' })
    expect(screen.queryByRole('button', { name: 'Ask or log' })).not.toBeInTheDocument()
  })

  it('opens and sends the sentence a page hands over, and shows the answer in the panel', async () => {
    const calls = setup()
    renderWithProviders(
      <>
        <AskButton />
        <ChatDock />
      </>,
    )

    await userEvent.click(screen.getByRole('button', { name: 'hand over' }))

    expect(await screen.findByRole('dialog', { name: 'Assistant' })).toBeInTheDocument()
    expect(await screen.findByText('Prepared your breakfast.')).toBeInTheDocument()
    const stream = calls.find((c) => c.url === '/api/chat/stream')!
    expect(JSON.parse(stream.init?.body as string)).toMatchObject({ message: 'two eggs for breakfast', conversationId: null })
  })

  it('keeps one conversation: a second sentence continues the same conversation', async () => {
    const calls = setup()
    renderWithProviders(
      <>
        <AskButton />
        <ChatDock />
      </>,
    )

    await userEvent.click(screen.getByRole('button', { name: 'hand over' }))
    await screen.findByText('Prepared your breakfast.')
    await userEvent.click(screen.getByRole('button', { name: 'hand over' }))
    await waitFor(() => expect(calls.filter((c) => c.url === '/api/chat/stream')).toHaveLength(2))

    const second = JSON.parse(calls.filter((c) => c.url === '/api/chat/stream')[1].init?.body as string)
    expect(second.conversationId).toBe('c1')
  })

  it('explains that AI processing must be allowed and does not let the user type without it', async () => {
    setup(false)
    renderWithProviders(<ChatDock />)
    await userEvent.click(screen.getByRole('button', { name: 'Ask or log' }))

    expect(await screen.findByText('AI processing is switched off')).toBeInTheDocument()
    expect(screen.getByLabelText('Ask anything about your wellness…')).toBeDisabled()
  })
})
