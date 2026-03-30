FROM golang:1.23-bookworm AS build
WORKDIR /src
COPY go-comparison/go.mod .
RUN go mod download 2>/dev/null; go mod tidy
COPY go-comparison/main.go .
RUN go mod tidy
RUN CGO_ENABLED=0 go build -o /bench .

FROM debian:bookworm-slim
COPY --from=build /bench /bench
ENTRYPOINT ["/bench"]
