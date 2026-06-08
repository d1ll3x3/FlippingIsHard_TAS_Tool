# SOLUCIÓN CORRECTA - Enfoque TAS Real

## PROBLEMA ACTUAL
Estamos intentando FORZAR la cámara escribiendo Camera.main.transform cada frame.
Esto causa:
- Parpadeos al iniciar/terminar replay
- Conflicto entre override manual y Cinemachine Brain
- Cámara en posiciones incorrectas

## SOLUCIÓN (Cómo funcionan TAS reales)

### 1. Durante REPLAY:
- **NO escribas Camera.main.transform NUNCA**
- **Cinemachine Brain SIEMPRE activo**
- **SOLO inyecta orbital axes cada physics tick**
- Deja que Cinemachine calcule la posición de cámara desde los axes

### 2. Durante SAVESTATE load:
- **NO uses override temporal**
- **Inyecta axes orbitales ANTES de cargar posición**
- **Llama a Cinemachine para forzar recalculo INMEDIATO**
- Deja que Cinemachine tome control

### 3. Durante EDIT MODE:
- **StopPlayback() NO reactiva Brain**
- **Brain se queda desactivado hasta salir de edit mode**
- Mientras grabas en edit mode, Brain sigue desactivado

## IMPLEMENTACIÓN

### StartPlayback() - NUEVO
```csharp
private void StartPlayback()
{
    _cachedRb = _gameObjectFinder.GetCachedPlayerRigidbody();
    if (_cachedRb != null)
    {
        _originalInterpolation = _cachedRb.interpolation;
        _cachedRb.interpolation = RigidbodyInterpolation.Interpolate;
    }
    _macroSystem.StartPlaying();
    
    // NO desactives Brain durante replay
    // ToggleCinemachine(false); ← ELIMINAR ESTO
    
    TASPlugin.Logger.LogInfo("TAS: Playback started");
}
```

### FixedUpdate() - NUEVO
```csharp
if (_macroSystem.IsPlaying)
{
    // Solo inyecta axes, NO toques Camera.main
    InjectPlaybackAxes();
    
    // NO escribas camera transform
    // Camera.main.transform.position = ... ← ELIMINAR
}
```

### Update() - NUEVO
```csharp
// ELIMINA este bloque completamente:
if (_macroSystem != null && _macroSystem.IsPlaying && Camera.main != null)
{
    float t = (Time.time - Time.fixedTime) / Time.fixedDeltaTime;
    // ...
}
```

### SavestateSystem - NUEVO
```csharp
public void LoadState(...)
{
    // Inyecta axes PRIMERO
    InjectOrbitalAxes(state);
    
    // Fuerza a Cinemachine a recalcular AHORA
    ForceCinemachineUpdate();
    
    // Luego carga posición del jugador
    rb.position = state.PlayerPosition;
    // ...
}
```

## RESULTADO ESPERADO
- ✅ Sin parpadeos (Cinemachine siempre controla la cámara)
- ✅ Determinismo perfecto (axes orbitales definen todo)
- ✅ Sin overrides temporales ni frames de transición
