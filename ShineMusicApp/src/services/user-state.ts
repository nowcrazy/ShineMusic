/**
 * Reset service-level data whenever the API origin or authenticated identity changes.
 * Page instances are also disposed on an origin change; generations in the services
 * below keep late responses from repopulating data for the previous scope.
 */
import { resetCatalogCaches } from './catalog'
import { resetCapabilities } from './capabilities'
import { resetFeedback } from './feedback'
import { resetLibrary } from './library'
import { resetNotifications } from './notifications'
import { resetDailyRecommendations } from './recommend'
import { resetSavedCollections } from './saved-collections'
import { applyTheme, currentTheme, DEFAULT_THEME, resetCustomTheme } from './theme'
import { resetMyLibrary } from './user-library'

export function resetUserApiState(): void {
  resetLibrary()
  resetMyLibrary()
  resetFeedback()
  resetNotifications()
  resetSavedCollections()
  resetDailyRecommendations()
  resetCatalogCaches()
  resetCapabilities()

  // Custom wallpapers and palettes are server-account data. Preset theme choices
  // remain local app preferences and are retained when changing API context.
  resetCustomTheme()
  if (currentTheme.value === 'custom') applyTheme(DEFAULT_THEME)
}
