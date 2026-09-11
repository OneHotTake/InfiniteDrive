#!/usr/bin/env ruby
# Generates a conservative settings wiring inventory from the source tree.
# It intentionally labels evidence, not inferred success.

root = File.expand_path('..', __dir__)
config = File.read(File.join(root, 'PluginConfiguration.cs'))
property_re = /public\s+(?!class\b|enum\b|const\b|static\b)([\w.<>,?]+)\s+(\w+)\s*\{\s*get;\s*set;\s*\}\s*(?:=\s*([^;]+);)?/
properties = config.scan(property_re)

ui_files = Dir[File.join(root, 'UI/Settings/*{UI,TabView}.cs')]
source_files = Dir[File.join(root, '**/*.cs')].reject do |path|
  path.include?('/bin/') || path.include?('/obj/') ||
    path.end_with?('/PluginConfiguration.cs') || path.include?('/UI/Settings/')
end
test_text = Dir[File.join(root, 'Tests/**/*.cs')].map { |p| File.read(p) }.join("\n")

live_verified = %w[
  PrimaryManifestUrl SecondaryManifestUrl EnableBackupAioStreams
  EnableAioStreamsCatalog AioStreamsCatalogIds CatalogItemLimitsJson
  SyncPathMovies SyncPathShows SyncPathAnime MaxVersionsPerItem
  DesiredVersions UseRemuxForAutoSelection AutoDeduplicatePhysicalMedia
  DeleteStrmOnReadoption MarvinProcessIntervalMinutes EnablePreCache
]

puts '# InfiniteDrive settings coverage matrix'
puts
puts 'Generated from the 0.42.1 source tree. “Wired” means a runtime source reference exists; it is not a claim that every behavior was exercised live.'
puts
puts '| Setting | Type / default | UI | Persistence | Runtime consumer(s) | Activation | Automated evidence | Staging evidence |'
puts '|---|---|---|---|---|---|---|---|'

properties.each do |type, name, default|
  ui = ui_files.select { |p| File.read(p).include?(name) }
               .map { |p| File.basename(p).sub(/(?:TabView|UI)\.cs$/, '') }
               .uniq
  ui_label = ui.empty? ? 'Internal / derived' : ui.join(', ')

  consumers = source_files.select { |p| File.read(p).match?(/\b#{Regexp.escape(name)}\b/) }
                          .map { |p| p.sub(root + '/', '') }
                          .uniq
  consumer_label = consumers.empty? ? '**No runtime reference found**' : consumers.first(4).join('<br>')
  consumer_label += '<br>…' if consumers.length > 4

  persisted = config.match?(/\[DataMember(?:\([^\]]*\))?\]\s*public\s+#{Regexp.escape(type)}\s+#{Regexp.escape(name)}\b/m)
  persistence = persisted ? 'Emby plugin XML (`DataMember`)' : '**Not persisted**'
  default_label = default ? default.strip.gsub('|', '\\|') : 'language default'
  tested = test_text.match?(/\b#{Regexp.escape(name)}\b/) ? 'Direct regression reference' : 'Persistence reflection only'
  staged = live_verified.include?(name) ? 'Verified in isolated Emby' : 'Not live-exercised'

  puts "| `#{name}` | `#{type}` / `#{default_label}` | #{ui_label} | #{persistence} | #{consumer_label} | Next invocation; no server restart | #{tested} | #{staged} |"
end
