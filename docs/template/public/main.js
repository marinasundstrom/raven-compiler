// Keep this lightweight browser lexer aligned with the full TextMate grammar in
// src/Raven.VSCode/syntaxes/raven.tmLanguage.json.
const raven = (hljs) => ({
  name: 'Raven',
  aliases: ['rav', 'rvn'],
  keywords: {
    keyword: [
      'abstract', 'add', 'alias', 'and', 'as', 'assembly', 'async', 'await',
      'base', 'break', 'by', 'case', 'catch', 'class', 'const', 'continue',
      'default', 'delegate', 'do', 'else', 'enum', 'event', 'explicit',
      'extension', 'extern', 'field', 'fileprivate', 'final', 'finally', 'fixed',
      'for', 'func', 'get', 'global', 'goto', 'if', 'implicit', 'import', 'in',
      'init', 'interface', 'internal', 'is', 'let', 'loop', 'macro', 'match',
      'method', 'module', 'namespace', 'new', 'nameof', 'not', 'notnull', 'open',
      'operator', 'or', 'out', 'override', 'param', 'parameter', 'params',
      'partial', 'permits', 'private', 'property', 'protected', 'public',
      'readonly', 'record', 'ref', 'remove', 'required', 'return', 'sealed',
      'scoped', 'self', 'set', 'sizeof', 'stackalloc', 'static', 'struct',
      'throw', 'try', 'type', 'typeof', 'union', 'unsafe', 'unmanaged', 'use',
      'val', 'var', 'lock',
      'virtual', 'when', 'where', 'while', 'with', 'yield',
      'expand', 'replace', 'introduce', 'fragment', 'token'
    ].join(' '),
    type: [
      'bool', 'byte', 'char', 'decimal', 'double', 'float', 'int', 'long',
      'nint', 'nuint', 'object', 'sbyte', 'short', 'string', 'uint', 'ulong',
      'unit', 'ushort', 'void', 'Option', 'Result', 'Task', 'ValueTask'
    ].join(' '),
    literal: 'true false null'
  },
  contains: [
    {
      scope: 'meta',
      begin: /^\s*#\s*pragma\b/,
      end: /$/,
      keywords: { keyword: 'warning disable-next-line disable restore' }
    },
    {
      scope: 'meta',
      begin: /#\[/,
      end: /\]/,
      contains: [
        { scope: 'title.function.invoke', begin: /[A-Za-z_][A-Za-z0-9_]*/ },
        hljs.QUOTE_STRING_MODE,
        hljs.C_NUMBER_MODE
      ]
    },
    hljs.COMMENT('///', '$', { contains: [{ scope: 'doctag', begin: /@[A-Za-z]+/ }] }),
    hljs.C_LINE_COMMENT_MODE,
    hljs.C_BLOCK_COMMENT_MODE,
    {
      scope: 'string',
      begin: /"""/,
      end: /"""(?:u8|ascii)?/,
      contains: [{ scope: 'subst', begin: /\$\{/, end: /\}/ }]
    },
    {
      scope: 'string',
      variants: [
        { begin: /"/, end: /"(?:u8|ascii)?/ },
        { begin: /'/, end: /'/ }
      ],
      contains: [
        hljs.BACKSLASH_ESCAPE,
        { scope: 'subst', begin: /\$\{/, end: /\}/ },
        { scope: 'subst', begin: /\$[A-Za-z_][A-Za-z0-9_]*/ }
      ]
    },
    {
      scope: 'number',
      variants: [
        { begin: /\b0[xX][0-9A-Fa-f](?:[0-9A-Fa-f_]*[0-9A-Fa-f])?\b/ },
        { begin: /\b0[bB][01](?:[01_]*[01])?\b/ },
        { begin: /\b\d+(?:_\d+)*(?:\.\d+(?:_\d+)*)?(?:[eE][+-]?\d+(?:_\d+)*)?[A-Za-z]*\b/ }
      ],
      relevance: 0
    },
    {
      scope: 'keyword',
      begin: /\bon(?=\s+(?:(?:[A-Za-z_][A-Za-z0-9_]*)\s*:\s*)?[A-Z_][A-Za-z0-9_.]*)/,
      relevance: 0
    },
    {
      // The static documentation renderer cannot resolve macro aliases. Keep
      // the aliases used by the public showcase visually aligned with their
      // contextual-keyword treatment in compiler-backed editors.
      scope: 'keyword',
      begin: /\b(?:component|markup)(?=\s*!)/,
      relevance: 0
    },
    {
      scope: 'title.function.invoke',
      begin: /\b[A-Za-z_][A-Za-z0-9_]*(?=\s*(?:<[^\r\n{}]*>)?\s*!\s*(?:\(|\{))/,
      relevance: 0
    },
    {
      beginKeywords: 'case',
      end: /(?=\s*(?:\(|$))/,
      contains: [
        { scope: 'type', begin: /[A-Za-z_][A-Za-z0-9_]*/ }
      ],
      relevance: 0
    },
    {
      begin: /\.(?=[A-Z][A-Za-z0-9_]*\s*\()/,
      end: /(?=\s*\()/,
      contains: [
        { scope: 'type', begin: /[A-Z][A-Za-z0-9_]*/ }
      ],
      relevance: 0
    },
    {
      scope: 'title.function',
      begin: /\b(?!(?:if|while|for|match|catch|typeof|nameof|sizeof|default|func|let|val|var|is|as|return|throw|new|init|public|internal|protected|private|fileprivate)\b)[A-Za-z_][A-Za-z0-9_]*(?=\s*\()/,
      relevance: 0
    },
    {
      scope: 'type',
      begin: /\b[A-Z][A-Za-z0-9_]*\b/,
      relevance: 0
    }
  ]
})

// Manual tabs keep code and installation commands stable while readers use them.
const initializeCarousels = () => {
  document.querySelectorAll('[data-raven-carousel], [data-raven-tabs]').forEach((group) => {
    const tabs = [...group.querySelectorAll('[role="tab"]')]
    const panels = tabs.map((tab) => document.getElementById(tab.getAttribute('aria-controls')))
    const select = (index, focus = false) => {
      tabs.forEach((tab, i) => {
        tab.setAttribute('aria-selected', String(i === index))
        tab.tabIndex = i === index ? 0 : -1
        panels[i].hidden = i !== index
      })
      if (focus) tabs[index].focus()
    }
    tabs.forEach((tab, index) => {
      tab.addEventListener('click', () => select(index))
      tab.addEventListener('keydown', (event) => {
        const next = {
          ArrowRight: (index + 1) % tabs.length,
          ArrowLeft: (index - 1 + tabs.length) % tabs.length,
          Home: 0,
          End: tabs.length - 1
        }[event.key]
        if (next !== undefined) {
          event.preventDefault()
          select(next, true)
        }
      })
    })
  })
}

const encodePlaygroundSource = (source) => {
  const bytes = new TextEncoder().encode(source)
  let binary = ''
  bytes.forEach((byte) => { binary += String.fromCharCode(byte) })
  return window.btoa(binary)
    .replace(/=+$/, '')
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
}

const initializePlaygroundSamples = () => {
  const docRoot = new URL(
    document.querySelector('meta[name="docfx:rel"]')?.content ?? '',
    document.baseURI)
  const playgroundBase = new URL('playground/', docRoot)

  document.querySelectorAll('[data-raven-playground]').forEach((marker) => {
    const codeBlock = marker.nextElementSibling
    if (codeBlock?.tagName !== 'PRE') return

    const example = marker.dataset.example
    const snippet = marker.dataset.snippet
    const useDisplayedSource = marker.dataset.ravenPlayground === 'source'
    if (!example && !snippet && !useDisplayedSource) return

    const playgroundUrl = new URL(playgroundBase)
    if (example) {
      playgroundUrl.searchParams.set('example', example)
      if (marker.dataset.run === 'true') playgroundUrl.searchParams.set('run', 'true')
    } else if (snippet) {
      playgroundUrl.searchParams.set('snippet', snippet)
      if (marker.dataset.run === 'true') playgroundUrl.searchParams.set('run', 'true')
    } else {
      playgroundUrl.searchParams.set(
        'source',
        encodePlaygroundSource(codeBlock.querySelector('code')?.textContent ?? ''))
    }

    const actions = document.createElement('div')
    actions.className = 'raven-sample-actions'

    if (marker.dataset.sourceUrl) {
      const sourceLink = document.createElement('a')
      sourceLink.href = marker.dataset.sourceUrl
      sourceLink.textContent = 'View source'
      actions.append(sourceLink)
    }

    const playgroundLink = document.createElement('a')
    playgroundLink.className = 'raven-playground-link'
    playgroundLink.href = playgroundUrl.href
    playgroundLink.target = '_blank'
    playgroundLink.rel = 'noopener'
    playgroundLink.textContent = example || snippet ? 'Try the complete example' : 'Try this code'
    actions.append(playgroundLink)

    codeBlock.insertAdjacentElement('afterend', actions)
  })
}

const initializeDocumentationNavigation = () => {
  if (document.querySelector('.raven-hero') || document.querySelector('#toc')) return
  const content = document.querySelector('main > .content')
  if (!content || content.querySelector('.raven-doc-navigation')) return
  const root = new URL(document.querySelector('meta[name="docfx:rel"]')?.content ?? '', document.baseURI)
  const details = document.createElement('details')
  details.className = 'raven-doc-navigation'
  details.open = window.matchMedia('(min-width: 992px)').matches
  const summary = document.createElement('summary')
  summary.textContent = 'Documentation'
  details.append(summary)
  const nav = document.createElement('nav')
  nav.setAttribute('aria-label', 'Documentation sections')
  const links = [
    ['Install and run', 'getting-started.html'],
    ['Learn Raven', 'learn.html'],
    ['Language tour', 'introduction.html'],
    ['For C# developers', 'raven-for-csharp-developers.html'],
    ['Language reference', 'lang/spec/index.html'],
    ['Build applications', 'workloads/index.html'],
    ['Tools and APIs', 'compiler/index.html'],
    ['Release status', 'status.html'],
    ['Contribute', 'https://github.com/marinasundstrom/raven/blob/main/CONTRIBUTING.md']
  ]
  links.forEach(([label, path]) => {
    const link = document.createElement('a')
    link.href = new URL(path, root).href
    link.textContent = label
    if (link.href === window.location.href.split('#')[0]) link.setAttribute('aria-current', 'page')
    nav.append(link)
  })
  details.append(nav)
  content.insertBefore(details, content.querySelector('article'))
}

const initializeReferenceFinder = () => {
  const finder = document.querySelector('[data-reference-finder]')
  if (!finder) return
  const input = finder.querySelector('input')
  const clear = finder.querySelector('[data-reference-clear]')
  const status = finder.querySelector('[data-reference-count]')
  const empty = document.querySelector('[data-reference-empty]')
  const shortcuts = document.querySelector('[data-reference-shortcuts]')
  const groups = [...document.querySelectorAll('.raven-reference-group')]
  const topics = [...document.querySelectorAll('[data-reference-topic]')].map((element) => ({
    element,
    text: `${element.textContent} ${element.dataset.keywords} ${element.closest('section').querySelector('h2').textContent}`.toLowerCase()
  }))
  const filter = () => {
    const terms = input.value.trim().toLowerCase().split(/\s+/).filter(Boolean)
    let visible = 0
    topics.forEach(({ element, text }) => {
      element.hidden = !terms.every((term) => text.includes(term))
      if (!element.hidden) visible++
    })
    groups.forEach((group) => {
      group.hidden = !group.querySelector('[data-reference-topic]:not([hidden])')
    })
    empty.hidden = visible !== 0
    if (shortcuts) shortcuts.hidden = terms.length > 0
    clear.disabled = input.value.length === 0
    status.textContent = terms.length ? `${visible} of ${topics.length} topics match.` : `${topics.length} reference topics. Filter by name, keyword, or syntax.`
    const url = new URL(window.location.href)
    if (input.value.trim()) url.searchParams.set('q', input.value.trim())
    else url.searchParams.delete('q')
    window.history.replaceState(null, '', url)
  }
  input.value = new URL(window.location.href).searchParams.get('q') ?? ''
  input.addEventListener('input', filter)
  clear.addEventListener('click', () => {
    input.value = ''
    filter()
    input.focus()
  })
  filter()
  finder.hidden = false
}

const initializeRavenSite = () => {
  initializeReferenceFinder()
  initializeDocumentationNavigation()
  initializeCarousels()
  initializePlaygroundSamples()
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', initializeRavenSite)
} else {
  initializeRavenSite()
}

export default {
  defaultTheme: 'auto',
  configureHljs(hljs) {
    hljs.registerLanguage('raven', raven)
  }
}
