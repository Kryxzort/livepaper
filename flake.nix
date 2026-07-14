{
  description = "livepaper-pro — live wallpaper manager for Wayland (video + Wallpaper Engine scenes)";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs =
    { self, nixpkgs }:
    let
      system = "x86_64-linux";
      pkgs = import nixpkgs { inherit system; };
      inherit (pkgs) lib;

      version = "0.1.0";

      # Runtime tools the backend shells out to. On NixOS these have no global FHS path, so we bake
      # them onto the wrapper's PATH — this is what makes livepaper "just work" without the user
      # installing ffmpeg/mpvpaper/etc. themselves.
      runtimeTools = with pkgs; [
        ffmpeg # /still thumbnail decode + frozen-frame grabs
        mpvpaper # video wallpapers (spawned via setsid)
        linux-wallpaperengine # Wallpaper Engine scene playback
        mpv # mpvpaper dep + still frames
      ];

      # 1) React UI → static dist/.
      ui = pkgs.buildNpmPackage {
        pname = "livepaper-ui";
        inherit version;
        src = ./app/ui;
        npmDepsHash = "sha256-CrYaDAHFfkcwSkwCWmApEtRufKyxB8Qh8UL6aK92q0k=";
        npmBuildScript = "build";
        installPhase = ''
          runHook preInstall
          mkdir -p $out
          cp -r dist/. $out/
          runHook postInstall
        '';
      };

      # 2) C# .NET 10 backend (headless web API + CLI). Self-contained so it carries its own runtime.
      backend = pkgs.buildDotnetModule {
        pname = "livepaper-backend";
        inherit version;
        src = ./src/livepaper;
        projectFile = "livepaper.csproj";
        nugetDeps = ./deps.json;
        dotnet-sdk = pkgs.dotnet-sdk_10;
        dotnet-runtime = pkgs.dotnet-runtime_10;
        selfContainedBuild = true;
        runtimeId = "linux-x64";
        # executable inside the csproj is named `livepaper`
        executables = [ "livepaper" ];
      };

      # 3) Native transition renderer (wlr-layer-shell + EGL/GLES3, libmpv full-live decode).
      # Absent → transitions no-op (instant cut); present → animated GLSL switches.
      lp-transition = pkgs.stdenv.mkDerivation {
        pname = "lp-transition";
        inherit version;
        src = ./src/native/lp-transition;
        nativeBuildInputs = [
          pkgs.pkg-config
          pkgs.wayland-scanner
        ];
        buildInputs = [
          pkgs.wayland # wayland-client + wayland-egl
          pkgs.libGL # egl + glesv2 (libglvnd)
          pkgs.mpv # libmpv render API
        ];
        installPhase = ''
          runHook preInstall
          install -Dm755 lp-transition $out/bin/lp-transition
          runHook postInstall
        '';
      };

      # 3b) Scene-audio crossfade helper (persistent libpulse context — event-driven volume).
      lp-audio = pkgs.stdenv.mkDerivation {
        pname = "lp-audio";
        inherit version;
        src = ./src/native/lp-audio;
        nativeBuildInputs = [ pkgs.pkg-config ];
        buildInputs = [ pkgs.libpulseaudio ];
        installPhase = ''
          runHook preInstall
          install -Dm755 lp-audio $out/bin/lp-audio
          runHook postInstall
        '';
      };

      # 4) Electron shell sources (plain JS; electron comes from nixpkgs, not the bundled prebuilt
      # which can't dlopen its libs on NixOS). Keep the 42.x pin — older crash-loops the GPU on
      # NVIDIA + kernel ≥6.12 (see .claude/rules/distribution.md).
      shellSrc = pkgs.runCommand "livepaper-shell-src" { } ''
        mkdir -p $out
        cp -r ${./app/shell}/. $out/
        rm -rf $out/node_modules
      '';

      livepaper = pkgs.stdenvNoCC.mkDerivation {
        pname = "livepaper";
        inherit version;
        dontUnpack = true;
        nativeBuildInputs = [ pkgs.makeWrapper ];

        installPhase = ''
          runHook preInstall
          mkdir -p $out/bin $out/share/livepaper $out/share/applications

          # transitions assets (shaders + manifest + preview frames)
          cp -r ${./transitions} $out/share/livepaper/transitions

          # GUI launcher: electron shell → spawns the backend (--serve) + loads the UI.
          makeWrapper ${pkgs.electron_42}/bin/electron $out/bin/livepaper-ui \
            --add-flags ${shellSrc} \
            --set LP_BACKEND ${backend}/bin/livepaper \
            --set LP_UI_DIR ${ui} \
            --set LP_TRANSITIONS_DIR $out/share/livepaper/transitions \
            --set LP_TRANSITION_BIN ${lp-transition}/bin/lp-transition \
            --set LP_AUDIO_BIN ${lp-audio}/bin/lp-audio \
            --prefix PATH : ${lib.makeBinPath runtimeTools}

          # Headless CLI (--restore/--action/daemons/--serve). Bare `livepaper` opens the GUI.
          makeWrapper ${backend}/bin/livepaper $out/bin/livepaper-cli \
            --set LP_UI_DIR ${ui} \
            --set LP_TRANSITIONS_DIR $out/share/livepaper/transitions \
            --set LP_TRANSITION_BIN ${lp-transition}/bin/lp-transition \
            --set LP_AUDIO_BIN ${lp-audio}/bin/lp-audio \
            --prefix PATH : ${lib.makeBinPath runtimeTools}

          cat > $out/bin/livepaper <<EOF
          #!${pkgs.runtimeShell}
          # bare invocation → GUI; any flag → headless backend
          if [ \$# -eq 0 ]; then exec $out/bin/livepaper-ui; fi
          exec $out/bin/livepaper-cli "\$@"
          EOF
          chmod +x $out/bin/livepaper

          cat > $out/share/applications/livepaper.desktop <<EOF
          [Desktop Entry]
          Name=Livepaper
          Comment=Live wallpaper manager (Wayland)
          Exec=$out/bin/livepaper-ui
          Type=Application
          Categories=Utility;
          Keywords=wallpaper;live;wayland;video;
          EOF

          runHook postInstall
        '';

        meta = {
          description = "Live wallpaper manager for Wayland (video + Wallpaper Engine scenes)";
          homepage = "https://github.com/kryxzort/livepaper-pro";
          platforms = [ "x86_64-linux" ];
          mainProgram = "livepaper";
        };
      };
    in
    {
      packages.${system} = {
        inherit ui backend lp-transition lp-audio livepaper;
        default = livepaper;
      };
      apps.${system}.default = {
        type = "app";
        program = "${livepaper}/bin/livepaper";
      };
    };
}
