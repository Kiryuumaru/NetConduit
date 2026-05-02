FROM golang:1.23-bookworm
WORKDIR /src
COPY go-comparison/go.mod .
COPY go-comparison/main.go .
RUN go mod tidy && CGO_ENABLED=0 go build -o /bench .
ENTRYPOINT ["/bench"]
