const audioExtensions = new Set(['flac', 'mp3', 'm4a', 'aac', 'ogg', 'oga', 'opus', 'wav', 'wave', 'ape', 'alac', 'aif', 'aiff', 'wv', 'audio'])
const contentExtensions: Record<string, string> = {
  'audio/flac': 'flac', 'audio/x-flac': 'flac', 'audio/mpeg': 'mp3', 'audio/mp4': 'm4a',
  'audio/aac': 'aac', 'audio/ogg': 'ogg', 'audio/opus': 'opus', 'audio/wav': 'wav',
  'audio/x-wav': 'wav', 'audio/aiff': 'aiff', 'audio/x-aiff': 'aiff',
}

export function isOriginalAudioContentType(value: string | null): boolean {
  const type = (value ?? '').split(';')[0].trim().toLowerCase()
  return type.startsWith('audio/') || type === 'application/octet-stream'
}

/** Treat the server filename as a suggestion, never as a path or executable extension. */
export function originalAudioFileName(disposition: string | null, title: string, contentType: string | null): string {
  const extension = contentExtensions[(contentType ?? '').split(';')[0].trim().toLowerCase()] ?? 'audio'
  let supplied = ''
  const encoded = disposition?.match(/(?:^|;)\s*filename\*\s*=\s*UTF-8'[^']*'([^;]*)/i)?.[1]
  if (encoded) {
    try { supplied = decodeURIComponent(encoded.trim()) } catch { /* use the plain filename or title */ }
  }
  if (!supplied) {
    const plain = disposition?.match(/(?:^|;)\s*filename\s*=\s*(?:"((?:[^"\\]|\\.)*)"|([^;]*))/i)
    supplied = plain?.[1]?.replace(/\\(.)/g, '$1') ?? plain?.[2]?.trim() ?? ''
  }
  let name = Array.from(supplied || `${title || 'track'}.${extension}`)
    .map(character => /[<>:"/\\|?*\p{Cc}\p{Cf}]/u.test(character) ? '_' : character)
    .slice(0, 180).join('').trim().replace(/^[. ]+|[. ]+$/g, '')
  if (!name) name = `track.${extension}`
  const suffix = name.match(/\.([a-z0-9]+)$/i)?.[1]?.toLowerCase()
  if (!suffix || !audioExtensions.has(suffix)) name += '.audio'
  if (/^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(name)) name = `_${name}`
  return name
}

/** Hand a completed attachment to the browser and release its temporary URL. */
export function saveOriginalAudioBlob(blob: Blob, name: string): void {
  const url = URL.createObjectURL(blob)
  let anchor: HTMLAnchorElement | undefined
  try {
    anchor = document.createElement('a')
    anchor.href = url
    anchor.download = name
    anchor.rel = 'noopener'
    document.body.appendChild(anchor)
    anchor.click()
  } finally {
    anchor?.remove()
    // A short delay lets the browser consume the URL before revocation.
    setTimeout(() => URL.revokeObjectURL(url), 1000)
  }
}
