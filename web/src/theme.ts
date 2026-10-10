// Dark is the default: echo and ECG rooms are dimmed. The choice is a per-browser convenience.
export type Theme = 'dark' | 'light'

const KEY = 'clinicreport.theme'

export function savedTheme(): Theme {
  try {
    return localStorage.getItem(KEY) === 'light' ? 'light' : 'dark'
  } catch {
    return 'dark'
  }
}

export function applyTheme(theme: Theme) {
  document.documentElement.dataset.theme = theme
  try {
    localStorage.setItem(KEY, theme)
  } catch {
    // private mode: the choice just isn't remembered
  }
}
