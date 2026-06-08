# DIAGNÓSTICO FINAL - Problema de Cámara en TAS

## PROBLEMA ACTUAL (versión GitHub)

Durante REPLAY:
```csharp
// Update() - 60+ FPS
if (_macroSystem.IsPlaying && Camera.main != null)
{
    Camera.main.transform.rotation = _macroSystem.GetInterpolatedCameraRotation(t);
    Camera.main.transform.position = _macroSystem.GetInterpolatedCameraPosition(t);
}

// FixedUpdate() - 50 FPS  
if (_macroSystem.IsPlaying && Camera.main != null)
{
    Camera.main.transform.rotation = _macroSystem.GetCurrentCameraRotation();
    Camera.main.transform.position = _macroSystem.GetCurrentCameraPosition();
}
```

**Brain está DESACTIVADO** durante replay (`ToggleCinemachine(false)`)
**PERO** estás escribiendo camera transform manualmente cada frame.

## POR QUÉ FALLA

1. **Al INICIAR playback (F10):**
   - Frame N-1: Brain activo, cámara en posición A
   - Frame N: `StartPlayback()` desactiva Brain, escribe posición B
   - **Si B ≠ A → PARPADEO visible**

2. **Al TERMINAR playback o EDIT MODE (F8):**
   - Frame N-1: Escribiendo posición A manualmente
   - Frame N: `StopPlayback()` reactiva Brain, Brain calcula posición B desde axes
   - **Si B ≠ A → PARPADEO visible**

3. **Durante SAVESTATE:**
   - Usas override temporal (5 frames)
   - Funciona PORQUE captura pan/tilt EXACTOS del savestate
   - Pero en replay, los pan/tilt grabados NO coinciden con las posiciones escritas

## SOLUCIÓN CORRECTA

**OPCIÓN 1: Usar SOLO Cinemachine (recomendado para TAS)**
- NO escribas Camera.main.transform NUNCA
- Brain SIEMPRE activo
- Solo inyecta orbital axes cada tick
- Cinemachine calcula posición determinísticamente

**OPCIÓN 2: Override completo (actual, pero arreglado)**
- Brain SIEMPRE desactivado durante replay
- Captura pan/tilt ANTES de iniciar
- Usa override temporal al iniciar/terminar
- Escribe camera transform

## MI RECOMENDACIÓN

**OPCIÓN 1** es la correcta para un TAS profesional porque:
- ✅ Sin parpadeos (transición suave)
- ✅ Determinista (axes → posición siempre igual)
- ✅ Sin overrides temporales ni hacks
- ✅ Cinemachine maneja suavizado automáticamente

¿Quieres que implemente OPCIÓN 1?
