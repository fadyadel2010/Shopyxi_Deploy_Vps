import { useLocation } from 'react-router-dom'
import AuthBootstrap from './AuthBootstrap'

export default function PosAuthBootstrapBoundary() {
    const { pathname } = useLocation()
    // These applications have their own session authorities. Never refresh POS
    // cookies (or clear its cache) merely because their pages gained focus.
    if (/^\/(super-admin|affiliate)(\/|$)/.test(pathname)) return null
    return <AuthBootstrap />
}
