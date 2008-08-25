#pragma once

#include "YamlEvent.h"
#include "ScalarStyle.h"
#include "INodeEvent.h"

using namespace System;
using namespace YamlDotNet::Core::LibYaml;

namespace YamlDotNet {
	namespace Core {
		public ref class ScalarEvent : public YamlEvent, public INodeEvent
		{
		private:
			String^ anchor;
			String^ tag;
			String^ value;
			ScalarStyle style;
			bool isPlainImplicit;
			bool isQuotedImplicit;

		internal:
			ScalarEvent(const yaml_event_t* nativeEvent);
			virtual void CreateEvent(yaml_event_t* nativeEvent) override;

		public:
			ScalarEvent(String^ value);
			ScalarEvent(String^ value, String^ tag);
			ScalarEvent(String^ value, String^ tag, String^ anchor);
			ScalarEvent(String^ value, String^ tag, String^ anchor, ScalarStyle style);
			ScalarEvent(String^ value, String^ tag, String^ anchor, ScalarStyle style, bool isPlainImplicit, bool isQuotedImplicit);
			virtual ~ScalarEvent();

			virtual property String^ Anchor {
				String^ get();
			}

			virtual property String^ Tag {
				String^ get();
			}

			property String^ Value {
				String^ get();
			}

			property int Length {
				int get();
			}

			property bool IsPlainImplicit {
				bool get();
			}

			property bool IsQuotedImplicit {
				bool get();
			}

			property ScalarStyle Style {
				ScalarStyle get();
			}

			virtual String^ ToString() override;
		};
	}
}