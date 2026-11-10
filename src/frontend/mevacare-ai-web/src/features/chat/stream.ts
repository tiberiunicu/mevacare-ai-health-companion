import { apiFetchRaw, toApiError } from '../../lib/api'
import type { ActionCard, Citation, ProposalCard } from '../../lib/types'

export type ChatStreamEvent =
  | { type: 'conversation'; conversationId: string; title: string }
  | { type: 'delta'; text: string }
  | { type: 'safety'; category: string }
  | { type: 'tool'; name: string; status: 'running' | 'done' | 'failed' }
  | { type: 'proposal'; card: ProposalCard }
  | { type: 'action'; card: ActionCard }
  | { type: 'citations'; items: Citation[] }
  | { type: 'done'; messageId: string }
  | { type: 'error'; code: string; message: string; messageId: string | null }

export interface SseBlock {
  event: string
  data: string
}

/** Parses one Server-Sent-Events block ("event: x\ndata: y"). Returns null for comments/keep-alives. */
export function parseSseBlock(block: string): SseBlock | null {
  let event = 'message'
  const data: string[] = []
  for (const rawLine of block.split('\n')) {
    const line = rawLine.endsWith('\r') ? rawLine.slice(0, -1) : rawLine
    if (line === '' || line.startsWith(':')) continue
    const separator = line.indexOf(':')
    const field = separator === -1 ? line : line.slice(0, separator)
    const value = separator === -1 ? '' : line.slice(separator + 1).replace(/^ /, '')
    if (field === 'event') event = value
    else if (field === 'data') data.push(value)
  }
  return data.length === 0 ? null : { event, data: data.join('\n') }
}

/** Splits a growing text buffer into complete SSE blocks and the unfinished remainder. */
export function splitSseBuffer(buffer: string): { blocks: string[]; rest: string } {
  const normalized = buffer.replace(/\r\n/g, '\n')
  const parts = normalized.split('\n\n')
  return { blocks: parts.slice(0, -1), rest: parts[parts.length - 1] }
}

export interface StreamChatRequest {
  conversationId: string | null
  message: string
  clientMessageId: string
  /** The user's local time with UTC offset, so the assistant knows today's date. */
  clientTime?: string
  signal: AbortSignal
  onEvent: (event: ChatStreamEvent) => void
}

/**
 * Sends a message and reports the streamed events. Pre-flight problems (consent, validation, provider not
 * configured...) reject with an ApiError before any event is emitted; problems after the stream started arrive as
 * an "error" event.
 */
export async function streamChat({ conversationId, message, clientMessageId, clientTime, signal, onEvent }: StreamChatRequest) {
  const response = await apiFetchRaw('/api/chat/stream', {
    method: 'POST',
    body: { conversationId, message, clientMessageId, clientTime },
    signal,
  })
  if (!response.ok) throw await toApiError(response)
  if (!response.body) throw new Error('The server did not return a stream.')

  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader()
  let buffer = ''
  for (;;) {
    const { value, done } = await reader.read()
    if (value) buffer += value
    const { blocks, rest } = splitSseBuffer(done ? `${buffer}\n\n` : buffer)
    buffer = rest
    for (const block of blocks) {
      const parsed = parseSseBlock(block)
      if (!parsed) continue
      onEvent({ type: parsed.event, ...(JSON.parse(parsed.data) as object) } as ChatStreamEvent)
    }
    if (done) break
  }
}
