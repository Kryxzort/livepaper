# lp-audio — libpulse helper for gapless scene-audio crossfades (event-driven volume).
CC      ?= cc
PKGS     = libpulse
CFLAGS  += -O2 -std=c11 -Wall -Wextra $(shell pkg-config --cflags $(PKGS))
LDFLAGS += $(shell pkg-config --libs $(PKGS)) -lpthread

PREFIX ?= $(HOME)/.local

lp-audio: lp-audio.c
	$(CC) $(CFLAGS) -o $@ lp-audio.c $(LDFLAGS)

install: lp-audio
	install -Dm755 lp-audio $(DESTDIR)$(PREFIX)/bin/lp-audio

clean:
	rm -f lp-audio

.PHONY: install clean
