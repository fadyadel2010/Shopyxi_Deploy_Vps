import { createContext, useCallback, useContext, useMemo, useState } from 'react'
import { Provider as RadixTooltipProvider } from '@radix-ui/react-tooltip'

// Surface identity is read at the hook call site (inside the POS Layout), not
// at the provider position, so global providers never need to move. The
// default protects Super Admin, Affiliate, and every other surface.
export const PosSurfaceContext = createContext('other')

export const PosOverlayContext = createContext(null)
// An overlay opened from a legacy confirmation must remain inside its visual
// stacking context. This is local to that confirmation, not a global portal.
export const PosNestedOverlayContext = createContext(null)

export function PosOverlayProvider({ children, surface = 'pos', tooltipDelayDuration = 500 }) {
    const [element, setElement] = useState(null)
    const registerHost = useCallback((node) => {
        setElement(node ?? null)
    }, [])

    const overlay = useMemo(() => ({ element, registerHost }), [element, registerHost])

    return (
        <PosSurfaceContext.Provider value={surface}>
            <PosOverlayContext.Provider value={overlay}>
                <RadixTooltipProvider delayDuration={tooltipDelayDuration}>
                    {children}
                </RadixTooltipProvider>
            </PosOverlayContext.Provider>
        </PosSurfaceContext.Provider>
    )
}

export function PosOverlayHost({ id = 'pos-overlay-root' }) {
    const overlay = useContext(PosOverlayContext)

    return <div id={id} data-pos-overlay-root="" ref={overlay?.registerHost} />
}

export function usePosOverlayElement() {
    const nestedElement = useContext(PosNestedOverlayContext)
    const overlay = useContext(PosOverlayContext)
    return nestedElement ?? overlay?.element ?? null
}

export function usePosSurface() {
    return useContext(PosSurfaceContext)
}
