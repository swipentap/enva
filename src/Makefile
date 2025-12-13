.PHONY: build clean run test

build:
	cd src && go build -o enva main.go

clean:
	rm -f src/enva src/enva.exe

run: build
	./src/enva

test:
	cd src && go test ./...

install-deps:
	cd src && go mod download
	cd src && go mod tidy

