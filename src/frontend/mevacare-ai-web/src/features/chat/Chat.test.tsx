import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useAuthStore } from '../../stores/auth'
import { json, mockFetch, renderWithProviders, signIn, useEnglish } from '../../test/utils'
import { ChatPage } from './ChatPage'
import { parseSseBlock, splitSseBuffer, streamChat } from './stream'

const consent = (granted: boolean) => [
  { type: 'AiProcessing', granted, required: false, policyVersion: '2026-10', grantedAtUtc: null, revokedAtUtc: null },
]

function sse(...events: [string, object][]) {
  const text = events.map(([name, data]) => `event: ${name}\ndata: ${JSON.stringify(data)}\n\n`).join('')
  // jsdom's Blob has no stream(); build the body stream by hand.
  const body = new ReadableStream({
    start(controller) {
      controller.enqueue(new TextEncoder().encode(text))
      controller.close()
    },
  })
  return new Response(body, { status: 200, headers: { 'Content-Type': 'text/event-stream' } })
}

describe('SSE parsing', () => {
  it('parses event and data fields and ignores comments', () => {
    expect(parseSseBlock(': keep-alive\nevent: delta\ndata: {"text":"hi"}')).toEqual({ event: 'delta', data: '{"text":"hi"}' })
    expect(parseSseBlock(': only a comment')).toBeNull()
  })

  it('splits a growing buffer into complete blocks and keeps the unfinished tail', () => {
    const { blocks, rest } = splitSseBuffer('event: a\ndata: 1\n\nevent: b\ndata: 2\r\n\r\nevent: c\nda')
    expect(blocks).toEqual(['event: a\ndata: 1', 'event: b\ndata: 2'])
    expect(rest).toBe('event: c\nda')
  })

  it('streams typed events from a response body even when chunks split an event', async () => {
    useAuthStore.getState().clear()
    const body = 'event: conversation\ndata: {"conversationId":"c1","title":"T"}\n\nevent: delta\ndata: {"text":"Hel"}\n\nevent: delta\ndata: {"text":"lo"}\n\nevent: done\ndata: {"messageId":"m1"}\n\n'
    const encoder = new TextEncoder()
    const stream = new ReadableStream({
      start(controller) {
        controller.enqueue(encoder.encode(body.slice(0, 40)))
        controller.enqueue(encoder.encode(body.slice(40)))
        controller.close()
      },
    })
    mockFetch({ 'POST /api/chat/stream': () => new Response(stream, { status: 200 }) })

    const types: string[] = []
    let text = ''
    await streamChat({
      conversationId: null,
      message: 'hi',
      clientMessageId: 'x',
      signal: new AbortController().signal,
      onEvent: (e) => {
        types.push(e.type)
        if (e.type === 'delta') text += e.text
      },
    })

    expect(types).toEqual(['conversation', 'delta', 'delta', 'done'])
    expect(text).toBe('Hello')
  })
})

describe('ChatPage', () => {
  beforeEach(async () => {
    await useEnglish()
    signIn()
  })
  afterEach(() => useAuthStore.getState().clear())

  it('blocks the input and points to AI settings while AI processing is not allowed', async () => {
    mockFetch({
      'GET /api/consents': () => json(consent(false)),
      'GET /api/conversations': () => json([]),
    })

    renderWithProviders(<ChatPage />)

    expect(await screen.findByText('AI processing is switched off')).toBeInTheDocument()
    expect(screen.getByLabelText('Ask anything about your wellness…')).toBeDisabled()
    expect(screen.getByRole('link', { name: 'Open AI Settings' })).toHaveAttribute('href', '/settings/ai')
  })

  it('streams the answer into the conversation and renders markdown', async () => {
    const { calls } = mockFetch({
      'GET /api/consents': () => json(consent(true)),
      'GET /api/conversations': () => json([]),
      'POST /api/chat/stream': () =>
        sse(
          ['conversation', { conversationId: 'c1', title: 'Explain HRV' }],
          ['delta', { text: 'HRV is **heart rate ' }],
          ['delta', { text: 'variability**.' }],
          ['done', { messageId: 'm1' }],
        ),
      'GET /api/conversations/c1/messages': () => json([]),
    })

    renderWithProviders(<ChatPage />)
    const input = await screen.findByLabelText('Ask anything about your wellness…')
    await waitFor(() => expect(input).toBeEnabled())
    await userEvent.type(input, 'Explain HRV{Enter}')

    const bold = await screen.findByText('heart rate variability')
    expect(bold.tagName).toBe('STRONG')
    expect(screen.getByText('Explain HRV')).toBeInTheDocument()
    const post = calls.find((c) => c.url === '/api/chat/stream')!
    const body = JSON.parse(post.init?.body as string)
    expect(body).toMatchObject({ message: 'Explain HRV', conversationId: null })
    expect(body.clientMessageId).toMatch(/^[0-9a-f-]{36}$/)
  })

  it('shows the provider outage message when the stream reports ai_unavailable', async () => {
    mockFetch({
      'GET /api/consents': () => json(consent(true)),
      'GET /api/conversations': () => json([]),
      'POST /api/chat/stream': () =>
        sse(['conversation', { conversationId: 'c1', title: 'Hello' }], ['error', { code: 'ai_unavailable', message: 'x', messageId: null }]),
      'GET /api/conversations/c1/messages': () => json([]),
    })

    renderWithProviders(<ChatPage />)
    const input = await screen.findByLabelText('Ask anything about your wellness…')
    await waitFor(() => expect(input).toBeEnabled())
    await userEvent.type(input, 'Hello{Enter}')

    expect(await screen.findByRole('alert')).toHaveTextContent('The AI service is not available right now')
  })

  it('restores the draft and shows a notice when the server rejects the request before streaming', async () => {
    mockFetch({
      'GET /api/consents': () => json(consent(true)),
      'GET /api/conversations': () => json([]),
      'POST /api/chat/stream': () => json({ code: 'ai_not_configured', detail: 'Ai:OpenAI:ApiKey missing' }, 503),
    })

    renderWithProviders(<ChatPage />)
    const input = await screen.findByLabelText('Ask anything about your wellness…')
    await waitFor(() => expect(input).toBeEnabled())
    await userEvent.type(input, 'Hello{Enter}')

    expect(await screen.findByText('The AI service is not available right now. Please try again in a moment.')).toBeInTheDocument()
    expect(input).toHaveValue('Hello')
    expect(screen.queryByText('Ai:OpenAI:ApiKey missing')).not.toBeInTheDocument()
  })

  it('flags fixed safety answers and never renders model HTML as markup', async () => {
    mockFetch({
      'GET /api/consents': () => json(consent(true)),
      'GET /api/conversations': () => json([]),
      'POST /api/chat/stream': () =>
        sse(
          ['conversation', { conversationId: 'c1', title: 'x' }],
          ['safety', { category: 'MedicalEmergency' }],
          ['delta', { text: 'Call 112 <img src=x onerror=alert(1)>' }],
          ['done', { messageId: 'm1' }],
        ),
      'GET /api/conversations/c1/messages': () => json([]),
    })

    const { container } = renderWithProviders(<ChatPage />)
    const input = await screen.findByLabelText('Ask anything about your wellness…')
    await waitFor(() => expect(input).toBeEnabled())
    await userEvent.type(input, 'I have crushing chest pain{Enter}')

    expect(await screen.findByText(/If you think this may be an emergency/)).toBeInTheDocument()
    expect(container.querySelector('img[src="x"]')).toBeNull()
  })
})
