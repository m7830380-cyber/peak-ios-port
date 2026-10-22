namespace PeakShim
{
    /// <summary>
    /// Render switches read by patched Unity code. RenderGraphUtils.CanAddCopyPass asks AllowCopyPass first: the
    /// render graph's copy pass reads its source through framebuffer fetch, which the converted CoreCopy shader
    /// only does since the converter learned it; until that is confirmed on the phone, copies use the ordinary
    /// blit (a texture read, the path every desktop GPU takes). PeakIOS persists the switch (peakios_fbcopy).
    /// </summary>
    public static class Gfx
    {
        public static bool FramebufferFetchCopy;

        public static bool AllowCopyPass() => FramebufferFetchCopy;
    }
}
